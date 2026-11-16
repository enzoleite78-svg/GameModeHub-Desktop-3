using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using GameModeHub.Core;
using GameModeHub.Services;

namespace GameModeHub.Server
{
    /// <summary>
    /// Servidor HTTP mínimo em TcpListener: não precisa de administrador nem de "urlacl".
    /// Segurança: só rede local, token obrigatório (exceto /ping e /pair), bloqueia requisições de navegador (Origin),
    /// e as ações perigosas (desligar, fechar programas…) só aceitam POST.
    /// 4.2: WebSocket em /ws para o mouse/teclado do celular sem atraso (uma conexão só, em vez de uma requisição por movimento).
    /// </summary>
    public sealed class ControlServer : IDisposable
    {
        private TcpListener _listener;
        private CancellationTokenSource _cts;
        public bool Rodando { get; private set; }
        public string Erro { get; private set; } = "";
        public event Action<string> CelularPareado;
        public event Action<string> Atividade;
        public event Action PedirModoTv;

        private static readonly JsonSerializerOptions JsonOpt = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        private readonly object _lockCodigo = new object();
        private string _codigo = NovoCodigo();
        private DateTime _codigoGeradoEm = DateTime.UtcNow;
        private static readonly TimeSpan ValidadeCodigo = TimeSpan.FromMinutes(10);
        private static string NovoCodigo() => RandomNumberGenerator.GetInt32(0, 1000000).ToString("D6");

        // ações que mudam muito o PC: só por POST (um link aberto sem querer não desliga nada)
        private static readonly HashSet<string> SoPost = new(StringComparer.OrdinalIgnoreCase)
            { "/desligar", "/reiniciar", "/suspender", "/kill", "/kill/ativo", "/timer", "/arquivo" };

        public string CodigoPareamento
        {
            get
            {
                lock (_lockCodigo)
                {
                    if (DateTime.UtcNow - _codigoGeradoEm > ValidadeCodigo) { _codigo = NovoCodigo(); _codigoGeradoEm = DateTime.UtcNow; }
                    return _codigo;
                }
            }
        }

        public TimeSpan CodigoRestante { get { lock (_lockCodigo) { var r = ValidadeCodigo - (DateTime.UtcNow - _codigoGeradoEm); return r < TimeSpan.Zero ? TimeSpan.Zero : r; } } }

        public void RegenerarCodigo() { lock (_lockCodigo) { _codigo = NovoCodigo(); _codigoGeradoEm = DateTime.UtcNow; } }

        private int _falhasPar; private DateTime _bloqueadoAte = DateTime.MinValue;

        public void Iniciar()
        {
            try
            {
                _cts = new CancellationTokenSource();
                _listener = new TcpListener(IPAddress.Any, ConfigService.Atual.Porta);
                _listener.Start(32);
                Rodando = true; Erro = "";
                _ = Task.Run(() => AceitarLoop(_cts.Token));
                ModeService.ModoAtivoMudou += AoMudarModo;
                TimerService.Mudou += AoMudarTimer;
                Log.Info("Servidor ouvindo na porta " + ConfigService.Atual.Porta);
            }
            catch (Exception ex)
            {
                Rodando = false;
                Erro = ex is SocketException se && se.SocketErrorCode == SocketError.AddressAlreadyInUse
                    ? $"a porta {ConfigService.Atual.Porta} já está em uso por outro programa (troque em Geral → Avançado)"
                    : ex.Message;
                Log.Error("Não consegui abrir a porta " + ConfigService.Atual.Porta, ex);
            }
        }

        private async Task AceitarLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var c = await _listener.AcceptTcpClientAsync(ct);
                    _ = Task.Run(() => Atender(c, ct));
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex) { Log.Warn("Accept: " + ex.Message); await Task.Delay(200); }
            }
        }

        private sealed class Req
        {
            public string Metodo, Caminho; public Dictionary<string, string> Query = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, string> Headers = new(StringComparer.OrdinalIgnoreCase); public string Corpo = "";
            public string ArquivoTemp;   // /arquivo: corpo gravado direto em disco
        }

        /// <summary>Resposta: corpo string = JSON; byte[] = imagem JPEG.</summary>
        private readonly struct Resp
        {
            public readonly int Status; public readonly object Corpo;
            public Resp(int s, object c) { Status = s; Corpo = c; }
        }

        private async Task Atender(TcpClient c, CancellationToken ct)
        {
            using (c)
            {
                try
                {
                    c.NoDelay = true;
                    var remoto = ((IPEndPoint)c.Client.RemoteEndPoint).Address;
                    if (!NetInfo.EhRedeLocal(remoto)) return;           // fora da rede local: derruba sem resposta

                    using var ns = c.GetStream();
                    var buf = new byte[16384];
                    var pend = new List<byte>(1024);

                    while (!ct.IsCancellationRequested)
                    {
                        var req = await LerRequisicao(ns, buf, pend, ct);
                        if (req == null) break;

                        // ---- WebSocket ----
                        if (req.Caminho.TrimEnd('/').Equals("/ws", StringComparison.OrdinalIgnoreCase) &&
                            req.Headers.TryGetValue("Upgrade", out var up) && up.Equals("websocket", StringComparison.OrdinalIgnoreCase))
                        {
                            if (req.Headers.ContainsKey("Origin") || !TokenValido(req)) { await Escrever(ns, new Resp(401, Json(new { ok = false })), false, ct); return; }
                            await WebSocketLoop(ns, req, pend, ct);
                            return;
                        }

                        Resp resp;
                        if (req.Metodo == "OPTIONS") resp = new Resp(403, Json(new { ok = false, erro = "origem bloqueada" }));
                        else
                        {
                            try { resp = await Rotear(req); }
                            catch (Exception ex) { Log.Error("Rota " + req.Caminho, ex); resp = new Resp(500, Json(new { ok = false, erro = "erro interno" })); }
                        }
                        if (req.ArquivoTemp != null) { try { File.Delete(req.ArquivoTemp); } catch { } }
                        await Escrever(ns, resp, req.Metodo == "HEAD", ct);
                    }
                }
                catch (Exception) { /* cliente caiu: normal */ }
            }
        }

        private static async Task Escrever(NetworkStream ns, Resp resp, bool semCorpo, CancellationToken ct)
        {
            byte[] corpo; string tipo;
            if (resp.Corpo is byte[] b) { corpo = b; tipo = "image/jpeg"; }
            else { corpo = Encoding.UTF8.GetBytes(resp.Corpo as string ?? "{}"); tipo = "application/json; charset=utf-8"; }
            var cab = $"HTTP/1.1 {resp.Status} {Texto(resp.Status)}\r\nContent-Type: {tipo}\r\nContent-Length: {corpo.Length}\r\nConnection: keep-alive\r\nCache-Control: no-store\r\n\r\n";
            var cb = Encoding.ASCII.GetBytes(cab);
            await ns.WriteAsync(cb, 0, cb.Length, ct);
            if (!semCorpo) await ns.WriteAsync(corpo, 0, corpo.Length, ct);
        }

        private static string Texto(int s) => s switch { 200 => "OK", 400 => "Bad Request", 401 => "Unauthorized", 403 => "Forbidden", 404 => "Not Found", 405 => "Method Not Allowed", 413 => "Payload Too Large", 429 => "Too Many Requests", _ => "Error" };

        private async Task<Req> LerRequisicao(NetworkStream ns, byte[] buf, List<byte> pend, CancellationToken ct)
        {
            int idxFim;
            while ((idxFim = Achar(pend)) < 0)
            {
                if (pend.Count > 32768) return null;
                using var to = CancellationTokenSource.CreateLinkedTokenSource(ct); to.CancelAfter(TimeSpan.FromSeconds(30));
                int n;
                try { n = await ns.ReadAsync(buf, 0, buf.Length, to.Token); } catch { return null; }
                if (n <= 0) return null;
                for (int i = 0; i < n; i++) pend.Add(buf[i]);
            }
            var cabTxt = Encoding.ASCII.GetString(pend.GetRange(0, idxFim).ToArray());
            pend.RemoveRange(0, idxFim + 4);
            var linhas = cabTxt.Split("\r\n");
            var p = linhas[0].Split(' ');
            if (p.Length < 2) return null;
            var r = new Req { Metodo = p[0].ToUpperInvariant() };
            var alvo = p[1]; int q = alvo.IndexOf('?');
            r.Caminho = q >= 0 ? alvo.Substring(0, q) : alvo;
            if (q >= 0) Preencher(r.Query, alvo.Substring(q + 1));
            for (int i = 1; i < linhas.Length; i++)
            {
                int d = linhas[i].IndexOf(':'); if (d > 0) r.Headers[linhas[i].Substring(0, d).Trim()] = linhas[i].Substring(d + 1).Trim();
            }
            if (r.Headers.TryGetValue("Content-Length", out var cl) && long.TryParse(cl, out long len) && len > 0)
            {
                // NOVO 4.2: arquivo enviado pelo celular vai direto para o disco (até 300 MB), e só depois do token conferido
                if (r.Caminho.TrimEnd('/').Equals("/arquivo", StringComparison.OrdinalIgnoreCase))
                {
                    if (len > 300L * 1024 * 1024 || !TokenValido(r)) return null;
                    r.ArquivoTemp = Path.Combine(Path.GetTempPath(), "gmh-" + Guid.NewGuid().ToString("N"));
                    using var fs = File.Create(r.ArquivoTemp);
                    long falta = len;
                    int deBuffer = (int)Math.Min(pend.Count, falta);
                    if (deBuffer > 0) { fs.Write(pend.GetRange(0, deBuffer).ToArray(), 0, deBuffer); pend.RemoveRange(0, deBuffer); falta -= deBuffer; }
                    while (falta > 0)
                    {
                        using var to = CancellationTokenSource.CreateLinkedTokenSource(ct); to.CancelAfter(TimeSpan.FromSeconds(30));
                        int n; try { n = await ns.ReadAsync(buf, 0, (int)Math.Min(buf.Length, falta), to.Token); } catch { return null; }
                        if (n <= 0) return null;
                        await fs.WriteAsync(buf, 0, n, ct); falta -= n;
                    }
                    return r;
                }
                if (len > 65536) return null;
                while (pend.Count < len)
                {
                    using var to = CancellationTokenSource.CreateLinkedTokenSource(ct); to.CancelAfter(TimeSpan.FromSeconds(30));
                    int n; try { n = await ns.ReadAsync(buf, 0, buf.Length, to.Token); } catch { return null; }
                    if (n <= 0) return null;
                    for (int i = 0; i < n; i++) pend.Add(buf[i]);
                }
                r.Corpo = Encoding.UTF8.GetString(pend.GetRange(0, (int)len).ToArray());
                pend.RemoveRange(0, (int)len);
                if (r.Corpo.TrimStart().StartsWith("{"))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(r.Corpo);
                        foreach (var pr in doc.RootElement.EnumerateObject()) r.Query[pr.Name] = pr.Value.ValueKind == JsonValueKind.String ? pr.Value.GetString() : pr.Value.ToString();
                    }
                    catch { }
                }
                else if ((r.Headers.TryGetValue("Content-Type", out var ct2) && ct2.Contains("x-www-form-urlencoded")) || r.Corpo.Contains('='))
                    Preencher(r.Query, r.Corpo);
            }
            return r;
        }

        private static int Achar(List<byte> b)
        {
            for (int i = 0; i + 3 < b.Count; i++)
                if (b[i] == 13 && b[i + 1] == 10 && b[i + 2] == 13 && b[i + 3] == 10) return i;
            return -1;
        }

        private static void Preencher(Dictionary<string, string> d, string qs)
        {
            foreach (var par in qs.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int e = par.IndexOf('=');
                var k = HttpUtility.UrlDecode(e >= 0 ? par.Substring(0, e) : par);
                var v = e >= 0 ? HttpUtility.UrlDecode(par.Substring(e + 1)) : "";
                d[k] = v;
            }
        }

        private static string Json(object o) => JsonSerializer.Serialize(o, JsonOpt);
        private static Resp Ok(object o) => new Resp(200, Json(o));

        private static string Str(Req r, params string[] nomes)
        {
            foreach (var n in nomes) if (r.Query.TryGetValue(n, out var v) && v != null) return v;
            return "";
        }

        private static int Int(Req r, int def, params string[] nomes)
        {
            var s = Str(r, nomes);
            if (int.TryParse(s, out var i)) return i;
            if (double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)) return (int)Math.Round(d);
            return def;
        }

        private static double Dbl(Req r, double def, params string[] nomes) =>
            double.TryParse(Str(r, nomes), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : def;

        private static bool Tem(Req r, params string[] nomes) => nomes.Any(n => r.Query.ContainsKey(n));

        private bool TokenValido(Req r)
        {
            string t = null;
            if (r.Headers.TryGetValue("X-GMH-Token", out var h)) t = h;
            else if (r.Headers.TryGetValue("Authorization", out var a) && a.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) t = a.Substring(7).Trim();
            else t = Str(r, "t", "token");
            if (string.IsNullOrEmpty(t)) return false;
            var c = ConfigService.Atual;
            if (FixedEq(t, c.Token)) return true;
            return c.Pareados.ToList().Any(p => FixedEq(t, p.Token));
        }

        private static bool FixedEq(string a, string b)
        {
            if (a == null || b == null) return false;
            return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
        }

        private async Task<Resp> Rotear(Req r)
        {
            // navegadores mandam Origin; o app do celular não. Bloqueia sites tentando controlar o PC.
            if (r.Headers.ContainsKey("Origin")) return new Resp(403, Json(new { ok = false, erro = "origem bloqueada" }));

            var rota = r.Caminho.TrimEnd('/').ToLowerInvariant();
            if (rota == "") rota = "/";

            // --- sem token ---
            if (rota == "/ping") return Ok(new { ok = true, status = "online", pc = Environment.MachineName, versao = AppInfo.Versao, porta = ConfigService.Atual.Porta, ws = true });
            if (rota == "/pair") return Parear(r);

            // --- com token ---
            if (!TokenValido(r)) return new Resp(401, Json(new { ok = false, erro = "não autorizado — pareie pelo QR" }));
            if (SoPost.Contains(rota) && r.Metodo != "POST") return new Resp(405, Json(new { ok = false, erro = "use POST para esta ação" }));

            var cfg = ConfigService.Atual;
            switch (rota)
            {
                case "/status": return Ok(Status());
                case "/apps": return Ok(new { apps = ProcessService.ListarAbertos().Select(a => new { exe = a.Exe, titulo = a.Titulo }) });

                // ---- áudio ----
                case "/audio/toggle": { var n = AudioService.Alternar(); Marcar("Áudio: " + n); return Ok(new { ok = n != null, saida = n, slot = cfg.AudioSlotAtivo }); }
                case "/audio/saida":
                    {
                        var slot = Str(r, "slot", "s").ToUpperInvariant() == "B" ? "B" : "A";
                        var n = AudioService.AplicarSlot(slot); Marcar("Áudio: " + n);
                        return Ok(new { ok = n != null, saida = n, slot = cfg.AudioSlotAtivo });
                    }
                case "/audio/saidas":
                    {
                        var lista = AudioService.ListarSaidas();
                        return Ok(new { saidas = lista.Select(s => new { id = s.Id, nome = s.Nome, padrao = s.Padrao, slot = s.Id == cfg.AudioSaidaACmd ? "A" : s.Id == cfg.AudioSaidaBCmd ? "B" : "" }) });
                    }
                case "/audio/definir": { var n = AudioService.DefinirSaidaPorId(Str(r, "id")); Marcar("Áudio: " + n); return Ok(new { ok = n != null, saida = n }); }
                case "/audio/volume":
                    if (Tem(r, "v", "valor", "nivel", "volume")) AudioService.DefinirVolume(Int(r, 50, "v", "valor", "nivel", "volume"));
                    else if (Tem(r, "d", "delta")) { var atual = AudioService.LerVolume(); if (atual >= 0) AudioService.DefinirVolume(atual + Int(r, 0, "d", "delta")); }
                    return Ok(new { ok = true, volume = AudioService.LerVolume(), mudo = AudioService.LerMudo() });
                case "/audio/mudo":
                    {
                        bool? alvo = Tem(r, "v", "valor") ? Int(r, 0, "v", "valor") != 0 : (bool?)null;
                        var m = AudioService.DefinirMudo(alvo);
                        return Ok(new { ok = m != null, mudo = m ?? false, volume = AudioService.LerVolume() });
                    }
                case "/audio/apps": return Ok(new { apps = AudioService.ListarApps().Select(a => new { pid = a.Pid, nome = a.Nome, volume = a.Volume, mudo = a.Mudo }) });
                case "/audio/app":
                    {
                        int? v = Tem(r, "v", "volume") ? Int(r, 100, "v", "volume") : (int?)null;
                        bool? m = Tem(r, "mudo") ? Int(r, 0, "mudo") != 0 : (bool?)null;
                        return Ok(new { ok = AudioService.DefinirApp(Int(r, -1, "pid"), v, m) });
                    }

                // ---- mídia ----
                case "/midia":
                    {
                        var i = await MediaService.Ler();
                        return Ok(new { tem = i.Tem, titulo = i.Titulo, artista = i.Artista, app = i.App, tocando = i.Tocando, capa = i.TemCapa });
                    }
                case "/midia/capa": { var b = await MediaService.Capa(); return b != null ? new Resp(200, b) : new Resp(404, Json(new { ok = false })); }
                case "/midia/acao": { var a = Str(r, "a", "acao"); var ok = a is "playpause" or "next" or "prev" ? await MediaService.Acao(a) : InputService.TeclaEspecial(a); return Ok(new { ok }); }

                // ---- modos / programas ----
                case "/restaurar": { var res = await Task.Run(() => ModeService.Restaurar()); Marcar("Restaurar"); return Ok(new { ok = res.Ok, resumo = res.Resumo }); }
                case "/kill":
                    {
                        var exe = Str(r, "exe", "nome");
                        if (string.IsNullOrWhiteSpace(exe)) { var n = await Task.Run(() => ProcessService.FecharJanelaAtiva()); Marcar("Fechou jogo em foco"); return Ok(new { ok = n != null, exe = n }); }
                        if (ProcessService.EhProtegido(exe)) return new Resp(403, Json(new { ok = false, erro = "app protegido" }));
                        var f = await Task.Run(() => ProcessService.Fechar(new[] { exe }, true));
                        Marcar("Fechou " + exe); return Ok(new { ok = f.Count > 0 });
                    }
                case "/kill/ativo": { var n = await Task.Run(() => ProcessService.FecharJanelaAtiva()); Marcar("Fechou jogo em foco"); return Ok(new { ok = n != null, exe = n }); }
                case "/alttab": InputService.AltTab(); return Ok(new { ok = true });
                case "/janela/focar": { var res = ProcessService.FocarOuAbrir(Str(r, "exe", "nome")); return Ok(new { ok = res.ok, abriu = res.abriu }); }
                case "/jogos": return Ok(new { jogos = GameLibraryService.Listar(Tem(r, "atualizar")).Select(j => new { id = j.Id, nome = j.Nome, loja = j.Loja, capa = j.Capa }) });
                case "/jogo/abrir": { var ok = GameLibraryService.Abrir(Str(r, "id")); Marcar("Jogo: " + Str(r, "id")); return Ok(new { ok }); }

                // ---- mouse / teclado (também existe pelo WebSocket /ws, mais rápido) ----
                case "/mouse/move": InputService.MoverMouse(Int(r, 0, "dx", "x"), Int(r, 0, "dy", "y")); return Ok(new { ok = true });
                case "/mouse/abs": InputService.MoverAbsoluto(Dbl(r, 0.5, "x"), Dbl(r, 0.5, "y")); if (Tem(r, "clique")) InputService.Clique(Str(r, "clique")); return Ok(new { ok = true });
                case "/mouse/click": InputService.Clique(Str(r, "btn", "botao") is var b2 && b2.Length > 0 ? b2 : "left", Str(r, "duplo", "double") is "1" or "true"); return Ok(new { ok = true });
                case "/mouse/scroll": InputService.Rolar(Int(r, 0, "d", "dy", "delta")); return Ok(new { ok = true });
                case "/mouse/hscroll": InputService.RolarHorizontal(Int(r, 0, "d", "dx", "delta")); return Ok(new { ok = true });
                case "/mouse/zoom": InputService.Zoom(Int(r, 0, "d", "delta", "dy")); return Ok(new { ok = true });
                case "/teclado":
                    {
                        var tk = Str(r, "tecla", "cmd");
                        bool ok = true;
                        if (!string.IsNullOrEmpty(tk)) ok = InputService.TeclaEspecial(tk, Str(r, "mods"));
                        var texto = Str(r, "texto", "text");
                        if (!string.IsNullOrEmpty(texto)) InputService.DigitarTexto(texto);
                        return Ok(new { ok });
                    }

                // ---- tela / desempenho ----
                case "/tela.jpg":
                case "/tela":
                    {
                        var img = await Task.Run(() => ScreenService.Capturar(Int(r, 960, "w"), Int(r, 55, "q")));
                        return img != null ? new Resp(200, img) : new Resp(500, Json(new { ok = false }));
                    }
                case "/desempenho":
                    {
                        var s = StatsService.Ler();
                        return Ok(new { cpu = s.cpu, ram = s.ramPct, ramUsada = Math.Round(s.ramUsadaGb, 1), ramTotal = Math.Round(s.ramTotalGb, 1), gpu = ScreenService.GpuPct() });
                    }

                // ---- área de transferência / arquivos ----
                case "/clipboard":
                    {
                        if (r.Metodo == "POST" || Tem(r, "texto"))
                        {
                            var texto = Str(r, "texto", "text");
                            bool abrir = Str(r, "abrir") is "1" or "true";
                            bool ok = Clipboard(texto);
                            if (abrir && Uri.TryCreate(texto.Trim(), UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https")) ProcessService.Abrir(u.ToString());
                            Views.ToastWindow.Mostrar(abrir ? "Link aberto pelo celular" : "Copiado do celular", texto.Length > 80 ? texto.Substring(0, 79) + "…" : texto, "📋");
                            return Ok(new { ok });
                        }
                        return Ok(new { ok = true, texto = LerClipboard() });
                    }
                case "/arquivo":
                    {
                        if (r.ArquivoTemp == null) return new Resp(400, Json(new { ok = false, erro = "arquivo vazio" }));
                        var nome = string.Concat((Str(r, "nome") is var nm && nm.Length > 0 ? nm : "arquivo").Split(Path.GetInvalidFileNameChars())).Trim();
                        if (nome.Length == 0) nome = "arquivo";
                        var pasta = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "GameModeHub");
                        Directory.CreateDirectory(pasta);
                        var destino = Path.Combine(pasta, nome);
                        for (int i = 2; File.Exists(destino); i++) destino = Path.Combine(pasta, Path.GetFileNameWithoutExtension(nome) + $" ({i})" + Path.GetExtension(nome));
                        File.Move(r.ArquivoTemp, destino);
                        r.ArquivoTemp = null;
                        Log.Info("Arquivo recebido do celular: " + destino);
                        Views.ToastWindow.Mostrar("Arquivo recebido do celular", Path.GetFileName(destino) + " · em Downloads\\GameModeHub", "📥");
                        return Ok(new { ok = true, caminho = destino });
                    }

                // ---- energia / timer ----
                case "/timer":
                    {
                        if (Str(r, "acao") == "cancelar") { TimerService.Cancelar(); return Ok(new { ok = true }); }
                        var ok = TimerService.Agendar(Str(r, "acao"), Int(r, 30, "min", "minutos"));
                        return Ok(new { ok, acao = TimerService.Acao, segundos = TimerService.SegundosRestantes });
                    }
                case "/tv": PedirModoTv?.Invoke(); Marcar("Modo TV"); return Ok(new { ok = true });
                case "/perfis": return Ok(new { ativo = cfg.PerfilAtivo, perfis = cfg.Perfis.ToList().Select(kv => new { id = kv.Key, nome = kv.Value.Nome }) });
                case "/perfil":
                    {
                        var id = Str(r, "id");
                        if (!cfg.Perfis.ContainsKey(id)) return new Resp(404, Json(new { ok = false, erro = "perfil não existe" }));
                        ConfigService.TrocarPerfil(id); Marcar("Perfil: " + ConfigService.Perfil.Nome);
                        return Ok(new { ok = true, perfil = cfg.PerfilAtivo });
                    }
                case "/suspender": Marcar("Suspender"); _ = Task.Run(async () => { await Task.Delay(800); PowerService.Suspender(); }); return Ok(new { ok = true });
                case "/desligar": Marcar("Desligar"); _ = Task.Run(async () => { await Task.Delay(800); ConfigService.SalvarAgora(); PowerService.Desligar(); }); return Ok(new { ok = true });
                case "/reiniciar": Marcar("Reiniciar"); _ = Task.Run(async () => { await Task.Delay(800); ConfigService.SalvarAgora(); PowerService.Reiniciar(); }); return Ok(new { ok = true });
            }

            if (rota.StartsWith("/modo/"))
            {
                var id = Uri.UnescapeDataString(r.Caminho.TrimEnd('/').Substring(6));
                var modo = ConfigService.Perfil.Modos.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase))
                        ?? ConfigService.Perfil.Modos.FirstOrDefault(m => string.Equals(m.Nome, id, StringComparison.OrdinalIgnoreCase));
                if (modo == null) return new Resp(404, Json(new { ok = false, erro = "modo não existe" }));
                var res = await Task.Run(() => ModeService.Ativar(modo.Id));
                Marcar("Modo: " + modo.Nome);
                return Ok(new { ok = res.Ok, resumo = res.Resumo, modoAtivo = ModeService.ModoAtivoId });
            }
            return new Resp(404, Json(new { ok = false, erro = "rota desconhecida" }));
        }

        // ---- área de transferência (precisa da thread da interface) ----
        private static bool Clipboard(string texto)
        {
            try
            {
                return System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    try { if (string.IsNullOrEmpty(texto)) System.Windows.Clipboard.Clear(); else System.Windows.Clipboard.SetText(texto); return true; }
                    catch { return false; }
                });
            }
            catch { return false; }
        }

        private static string LerClipboard()
        {
            try
            {
                return System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    try { return System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : ""; } catch { return ""; }
                });
            }
            catch { return ""; }
        }

        private Resp Parear(Req r)
        {
            lock (_lockCodigo)
            {
                if (DateTime.UtcNow < _bloqueadoAte) return new Resp(429, Json(new { ok = false, erro = "muitas tentativas, aguarde 1 minuto" }));
            }
            if (!FixedEq(Str(r, "code", "codigo").Replace(" ", ""), CodigoPareamento))
            {
                lock (_lockCodigo) { if (++_falhasPar >= 5) { _bloqueadoAte = DateTime.UtcNow.AddMinutes(1); _falhasPar = 0; } }
                return new Resp(401, Json(new { ok = false, erro = "código incorreto" }));
            }
            _falhasPar = 0;
            var nome = Str(r, "nome", "name").Trim();
            if (nome.Length == 0) nome = "Celular";
            if (nome.Length > 40) nome = nome.Substring(0, 40);
            var novo = new DispositivoPareado { Nome = nome, Token = ConfigService.NovoToken() };
            ConfigService.Atual.Pareados.Add(novo);
            ConfigService.Salvar(true);
            RegenerarCodigo();
            CelularPareado?.Invoke(nome);
            Log.Info("Celular pareado: " + nome);
            var net = NetInfo.Principal();
            return Ok(new { ok = true, token = novo.Token, pc = Environment.MachineName, mac = net?.Mac ?? "" });
        }

        private object Status()
        {
            var c = ConfigService.Atual;
            var s = StatsService.Ler();
            var perfil = ConfigService.Perfil;
            var (exe, titulo) = ScreenService.JanelaAtiva();
            return new
            {
                ok = true, pc = Environment.MachineName, versao = AppInfo.Versao,
                modoAtivo = ModeService.ModoAtivoId,
                ocupado = ModeService.Ocupado,
                perfil = c.PerfilAtivo, perfilNome = perfil.Nome,
                modos = perfil.Modos.ToList().Select(m => new { id = m.Id, nome = m.Nome, cor = m.Cor, atalho = m.TeclaAtalho, ativo = m.Id == ModeService.ModoAtivoId }),
                volume = AudioService.LerVolume(), mudo = AudioService.LerMudo(), slotAudio = c.AudioSlotAtivo,
                saidaA = c.AudioSaidaANome, saidaB = c.AudioSaidaBNome,
                cpu = s.cpu, ram = s.ramPct,
                janela = exe, janelaTitulo = titulo,
                timer = TimerService.Acao, timerSeg = TimerService.SegundosRestantes,
                desfoque = c.DesfoqueWallpaper, escurecer = c.NivelFoscoWallpaper
            };
        }

        private void Marcar(string t) { Atividade?.Invoke(t); Log.Info("Celular → " + t); }

        // ================= WEBSOCKET =================
        private sealed class ClienteWs
        {
            public NetworkStream Ns; public readonly SemaphoreSlim Trava = new SemaphoreSlim(1, 1);
        }
        private readonly List<ClienteWs> _ws = new();

        private async Task WebSocketLoop(NetworkStream ns, Req req, List<byte> pend, CancellationToken ct)
        {
            var chave = req.Headers.TryGetValue("Sec-WebSocket-Key", out var k) ? k : "";
            var aceite = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(chave + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            var cab = Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {aceite}\r\n\r\n");
            await ns.WriteAsync(cab, 0, cab.Length, ct);
            var cli = new ClienteWs { Ns = ns };
            lock (_ws) _ws.Add(cli);
            Log.Info("Celular conectado (tempo real)");
            var buf = new List<byte>(pend);
            var leitura = new byte[8192];
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    // precisa de pelo menos o cabeçalho do quadro
                    var quadro = TentarQuadro(buf);
                    if (quadro == null)
                    {
                        using var to = CancellationTokenSource.CreateLinkedTokenSource(ct); to.CancelAfter(TimeSpan.FromSeconds(45));
                        int n = await ns.ReadAsync(leitura, 0, leitura.Length, to.Token);
                        if (n <= 0) break;
                        for (int i = 0; i < n; i++) buf.Add(leitura[i]);
                        if (buf.Count > 1 << 20) break;
                        continue;
                    }
                    var (op, dados) = quadro.Value;
                    if (op == 8) break;                                            // fechar
                    if (op == 9) { await EnviarQuadro(cli, 10, dados); continue; }  // ping → pong
                    if (op != 1) continue;
                    foreach (var linha in Encoding.UTF8.GetString(dados).Split('\n'))
                        if (linha.Length > 0) { try { await ComandoWs(cli, linha); } catch (Exception ex) { Log.Warn("WS: " + ex.Message); } }
                }
            }
            catch { }
            finally { lock (_ws) _ws.Remove(cli); Log.Info("Celular desconectou (tempo real)"); }
        }

        private static (int op, byte[] dados)? TentarQuadro(List<byte> b)
        {
            if (b.Count < 2) return null;
            int op = b[0] & 0x0F; bool mask = (b[1] & 0x80) != 0; long len = b[1] & 0x7F; int pos = 2;
            if (len == 126) { if (b.Count < 4) return null; len = (b[2] << 8) | b[3]; pos = 4; }
            else if (len == 127) { if (b.Count < 10) return null; len = 0; for (int i = 2; i < 10; i++) len = (len << 8) | b[i]; pos = 10; }
            if (len > 1 << 20) throw new IOException("quadro grande demais");
            int total = pos + (mask ? 4 : 0) + (int)len;
            if (b.Count < total) return null;
            var dados = new byte[len];
            for (int i = 0; i < len; i++)
            {
                byte v = b[pos + (mask ? 4 : 0) + i];
                if (mask) v ^= b[pos + (i % 4)];
                dados[i] = v;
            }
            b.RemoveRange(0, total);
            return (op, dados);
        }

        private static async Task EnviarQuadro(ClienteWs c, int op, byte[] dados)
        {
            var cab = new List<byte> { (byte)(0x80 | op) };
            if (dados.Length < 126) cab.Add((byte)dados.Length);
            else if (dados.Length <= 65535) { cab.Add(126); cab.Add((byte)(dados.Length >> 8)); cab.Add((byte)dados.Length); }
            else { cab.Add(127); for (int i = 7; i >= 0; i--) cab.Add((byte)((long)dados.Length >> (8 * i))); }
            await c.Trava.WaitAsync();
            try { await c.Ns.WriteAsync(cab.ToArray()); await c.Ns.WriteAsync(dados); }
            finally { c.Trava.Release(); }
        }

        private static Task EnviarTexto(ClienteWs c, string t) => EnviarQuadro(c, 1, Encoding.UTF8.GetBytes(t));

        /// <summary>
        /// Protocolo de linhas (o celular junta vários movimentos por quadro):
        /// m dx dy · s d · h d · z d · c btn · cc btn · d btn · u btn · a x y (0-1) · k tecla [mods] · t texto · p (ping)
        /// </summary>
        private static async Task ComandoWs(ClienteWs cli, string linha)
        {
            var esp = linha.IndexOf(' ');
            var cmd = esp < 0 ? linha : linha.Substring(0, esp);
            var resto = esp < 0 ? "" : linha.Substring(esp + 1);
            var p = resto.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int I(int i) => i < p.Length && int.TryParse(p[i], out var v) ? v : 0;
            double D(int i) => i < p.Length && double.TryParse(p[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
            string S(int i) => i < p.Length ? p[i] : "";
            switch (cmd)
            {
                case "m": InputService.MoverMouse(I(0), I(1)); break;
                case "s": InputService.Rolar(I(0)); break;
                case "h": InputService.RolarHorizontal(I(0)); break;
                case "z": InputService.Zoom(I(0)); break;
                case "c": InputService.Clique(S(0)); break;
                case "cc": InputService.Clique(S(0), true); break;
                case "d": InputService.Botao(S(0), true); break;
                case "u": InputService.Botao(S(0), false); break;
                case "a": InputService.MoverAbsoluto(D(0), D(1)); break;
                case "k": InputService.TeclaEspecial(S(0), S(1)); break;
                case "t": InputService.DigitarTexto(resto); break;
                case "p": await EnviarTexto(cli, "p"); break;
            }
        }

        /// <summary>Envia um aviso para todos os celulares conectados ("evt {json}").</summary>
        public void Transmitir(object evento)
        {
            var msg = "evt " + Json(evento);
            ClienteWs[] todos;
            lock (_ws) todos = _ws.ToArray();
            foreach (var c in todos) _ = EnviarTexto(c, msg).ContinueWith(_ => { }, TaskScheduler.Default);
        }

        private void AoMudarModo(string id)
        {
            var m = ConfigService.Perfil.Modos.FirstOrDefault(x => x.Id == id);
            Transmitir(new { tipo = "modo", id, nome = m?.Nome ?? "Desktop normal" });
        }

        private void AoMudarTimer() => Transmitir(new { tipo = "timer", acao = TimerService.Acao, segundos = TimerService.SegundosRestantes });

        public void Dispose()
        {
            ModeService.ModoAtivoMudou -= AoMudarModo;
            TimerService.Mudou -= AoMudarTimer;
            try { _cts?.Cancel(); _listener?.Stop(); } catch { }
            ClienteWs[] todos; lock (_ws) todos = _ws.ToArray();
            foreach (var c in todos) { try { c.Ns.Close(); } catch { } }
            Rodando = false;
        }
    }
}
