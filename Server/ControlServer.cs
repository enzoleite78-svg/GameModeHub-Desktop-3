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
    /// Segurança: só rede local, token obrigatório (exceto /ping e /pair), bloqueia requisições de navegador (Origin).
    /// Compatível com os nomes de parâmetro do app do celular antigo (nivel, nome, dy, delta, cmd) e com os novos.
    /// </summary>
    public sealed class ControlServer : IDisposable
    {
        private TcpListener _listener;
        private CancellationTokenSource _cts;
        public bool Rodando { get; private set; }
        public string Erro { get; private set; } = "";
        public event Action<string> CelularPareado;
        public event Action<string> Atividade;        // "Celular: modo TV" etc. (para o log)
        public event Action PedirModoTv;              // NOVO 4.1: rota /tv abre o Modo TV no PC

        private static readonly JsonSerializerOptions JsonOpt = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        private readonly object _lockCodigo = new object();
        private string _codigo = NovoCodigo();
        private DateTime _codigoGeradoEm = DateTime.UtcNow;
        private static readonly TimeSpan ValidadeCodigo = TimeSpan.FromMinutes(10);
        private static string NovoCodigo() => RandomNumberGenerator.GetInt32(0, 1000000).ToString("D6");

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

        /// <summary>Quanto tempo o código atual ainda vale (a tela mostra a contagem).</summary>
        public TimeSpan CodigoRestante { get { lock (_lockCodigo) { var r = ValidadeCodigo - (DateTime.UtcNow - _codigoGeradoEm); return r < TimeSpan.Zero ? TimeSpan.Zero : r; } } }

        public void RegenerarCodigo() { lock (_lockCodigo) { _codigo = NovoCodigo(); _codigoGeradoEm = DateTime.UtcNow; } }

        // anti força-bruta do código de pareamento
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
                        // lê cabeçalho (com timeout de 30 s de ociosidade p/ keep-alive)
                        var req = await LerRequisicao(ns, buf, pend, ct);
                        if (req == null) break;

                        (int status, string json) resp;
                        if (req.Metodo == "OPTIONS") resp = (403, Json(new { ok = false, erro = "origem bloqueada" }));
                        else
                        {
                            try { resp = await Rotear(req, remoto); }
                            catch (Exception ex) { Log.Error("Rota " + req.Caminho, ex); resp = (500, Json(new { ok = false, erro = "erro interno" })); }
                        }

                        var corpo = Encoding.UTF8.GetBytes(resp.json);
                        var cab = $"HTTP/1.1 {resp.status} {Texto(resp.status)}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {corpo.Length}\r\nConnection: keep-alive\r\nCache-Control: no-store\r\n\r\n";
                        var cb = Encoding.ASCII.GetBytes(cab);
                        await ns.WriteAsync(cb, 0, cb.Length, ct);
                        if (req.Metodo != "HEAD") await ns.WriteAsync(corpo, 0, corpo.Length, ct);
                    }
                }
                catch (Exception) { /* cliente caiu: normal */ }
            }
        }

        private static string Texto(int s) => s switch { 200 => "OK", 400 => "Bad Request", 401 => "Unauthorized", 403 => "Forbidden", 404 => "Not Found", 429 => "Too Many Requests", _ => "Error" };

        private static async Task<Req> LerRequisicao(NetworkStream ns, byte[] buf, List<byte> pend, CancellationToken ct)
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
            if (r.Headers.TryGetValue("Content-Length", out var cl) && int.TryParse(cl, out int len) && len > 0)
            {
                if (len > 65536) return null;
                while (pend.Count < len)
                {
                    using var to = CancellationTokenSource.CreateLinkedTokenSource(ct); to.CancelAfter(TimeSpan.FromSeconds(30));
                    int n; try { n = await ns.ReadAsync(buf, 0, buf.Length, to.Token); } catch { return null; }
                    if (n <= 0) return null;
                    for (int i = 0; i < n; i++) pend.Add(buf[i]);
                }
                r.Corpo = Encoding.UTF8.GetString(pend.GetRange(0, len).ToArray());
                pend.RemoveRange(0, len);
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

        /// <summary>Lê o primeiro parâmetro que existir entre os nomes dados (compatibilidade com o app antigo).</summary>
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

        private static bool Tem(Req r, params string[] nomes) => nomes.Any(n => r.Query.ContainsKey(n));

        private bool TokenValido(Req r)
        {
            var t = r.Headers.TryGetValue("X-GMH-Token", out var h) ? h : Str(r, "t", "token");
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

        private async Task<(int, string)> Rotear(Req r, IPAddress remoto)
        {
            // navegadores mandam Origin; o app do celular não. Bloqueia sites tentando controlar o PC.
            if (r.Headers.ContainsKey("Origin")) return (403, Json(new { ok = false, erro = "origem bloqueada" }));

            var rota = r.Caminho.TrimEnd('/').ToLowerInvariant();
            if (rota == "") rota = "/";

            // --- sem token ---
            if (rota == "/ping") return (200, Json(new { ok = true, status = "online", pc = Environment.MachineName, versao = AppInfo.Versao, porta = ConfigService.Atual.Porta }));
            if (rota == "/pair") return Parear(r);

            // --- com token ---
            if (!TokenValido(r)) return (401, Json(new { ok = false, erro = "não autorizado — pareie pelo QR" }));

            var cfg = ConfigService.Atual;
            switch (rota)
            {
                case "/status": return (200, Json(Status()));
                case "/apps": return (200, Json(new { apps = ProcessService.ListarAbertos().Select(a => new { exe = a.Exe, titulo = a.Titulo }) }));

                case "/audio/toggle": { var n = AudioService.Alternar(); Marcar("Áudio: " + n); return (200, Json(new { ok = n != null, saida = n, slot = cfg.AudioSlotAtivo })); }
                case "/audio/saida":
                    {
                        var slot = Str(r, "slot", "s").ToUpperInvariant() == "B" ? "B" : "A";
                        var n = AudioService.AplicarSlot(slot); Marcar("Áudio: " + n);
                        return (200, Json(new { ok = n != null, saida = n, slot = cfg.AudioSlotAtivo }));
                    }
                case "/audio/volume":
                    if (Tem(r, "v", "valor", "nivel", "volume")) AudioService.DefinirVolume(Int(r, 50, "v", "valor", "nivel", "volume"));
                    else if (Tem(r, "d", "delta")) { var atual = AudioService.LerVolume(); if (atual >= 0) AudioService.DefinirVolume(atual + Int(r, 0, "d", "delta")); }
                    return (200, Json(new { ok = true, volume = AudioService.LerVolume(), mudo = AudioService.LerMudo() }));
                case "/audio/mudo":
                    {
                        bool? alvo = Tem(r, "v", "valor") ? Int(r, 0, "v", "valor") != 0 : (bool?)null;
                        var m = AudioService.DefinirMudo(alvo);
                        return (200, Json(new { ok = m != null, mudo = m ?? false, volume = AudioService.LerVolume() }));
                    }
                case "/restaurar": { var res = await Task.Run(() => ModeService.Restaurar()); Marcar("Restaurar"); return (200, Json(new { ok = res.Ok, resumo = res.Resumo })); }
                case "/kill":
                    {
                        // CORREÇÃO 4.1: sem "exe" (app antigo) fecha o jogo em foco, como na v3. Antes não fazia nada.
                        var exe = Str(r, "exe", "nome");
                        if (string.IsNullOrWhiteSpace(exe)) { var n = await Task.Run(() => ProcessService.FecharJanelaAtiva()); Marcar("Fechou jogo em foco"); return (200, Json(new { ok = n != null, exe = n })); }
                        if (ProcessService.EhProtegido(exe)) return (403, Json(new { ok = false, erro = "app protegido" }));
                        var f = await Task.Run(() => ProcessService.Fechar(new[] { exe }, true));
                        Marcar("Fechou " + exe); return (200, Json(new { ok = f.Count > 0 }));
                    }
                case "/kill/ativo": { var n = await Task.Run(() => ProcessService.FecharJanelaAtiva()); return (200, Json(new { ok = n != null, exe = n })); }
                case "/alttab": InputService.AltTab(); return (200, Json(new { ok = true }));
                case "/janela/focar":
                    {
                        var res = ProcessService.FocarOuAbrir(Str(r, "exe", "nome"));
                        return (200, Json(new { ok = res.ok, abriu = res.abriu }));
                    }

                case "/mouse/move": InputService.MoverMouse(Int(r, 0, "dx", "x"), Int(r, 0, "dy", "y")); return (200, "{\"ok\":true}");
                case "/mouse/click": InputService.Clique(Str(r, "btn", "botao") is var b && b.Length > 0 ? b : "left", Str(r, "duplo", "double") is "1" or "true"); return (200, "{\"ok\":true}");
                case "/mouse/scroll": InputService.Rolar(Int(r, 0, "d", "dy", "delta")); return (200, "{\"ok\":true}");
                case "/mouse/zoom": InputService.Zoom(Int(r, 0, "d", "delta", "dy")); return (200, "{\"ok\":true}");
                case "/teclado":
                    {
                        var tk = Str(r, "tecla", "cmd");
                        bool ok = true;
                        if (!string.IsNullOrEmpty(tk)) ok = InputService.TeclaEspecial(tk);
                        var texto = Str(r, "texto", "text");
                        if (!string.IsNullOrEmpty(texto)) InputService.DigitarTexto(texto);
                        return (200, Json(new { ok }));
                    }

                case "/tv": PedirModoTv?.Invoke(); Marcar("Modo TV"); return (200, Json(new { ok = true }));
                case "/perfis": return (200, Json(new { ativo = cfg.PerfilAtivo, perfis = cfg.Perfis.ToList().Select(kv => new { id = kv.Key, nome = kv.Value.Nome }) }));
                case "/perfil":
                    {
                        var id = Str(r, "id");
                        if (!cfg.Perfis.ContainsKey(id)) return (404, Json(new { ok = false, erro = "perfil não existe" }));
                        ConfigService.TrocarPerfil(id); Marcar("Perfil: " + ConfigService.Perfil.Nome);
                        return (200, Json(new { ok = true, perfil = cfg.PerfilAtivo }));
                    }

                case "/suspender": Marcar("Suspender"); _ = Task.Run(async () => { await Task.Delay(800); PowerService.Suspender(); }); return (200, Json(new { ok = true }));
                case "/desligar": Marcar("Desligar"); _ = Task.Run(async () => { await Task.Delay(800); PowerService.Desligar(); }); return (200, Json(new { ok = true }));
                case "/reiniciar": Marcar("Reiniciar"); _ = Task.Run(async () => { await Task.Delay(800); PowerService.Reiniciar(); }); return (200, Json(new { ok = true }));
            }

            if (rota.StartsWith("/modo/"))
            {
                // CORREÇÃO 4.1: usa o caminho original (sem minúsculas) para decodificar o id
                var id = Uri.UnescapeDataString(r.Caminho.TrimEnd('/').Substring(6));
                // ids podem ter maiúsculas: procura sem diferenciar
                var modo = ConfigService.Perfil.Modos.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase))
                        ?? ConfigService.Perfil.Modos.FirstOrDefault(m => string.Equals(m.Nome, id, StringComparison.OrdinalIgnoreCase));
                if (modo == null) return (404, Json(new { ok = false, erro = "modo não existe" }));
                var res = await Task.Run(() => ModeService.Ativar(modo.Id));
                Marcar("Modo: " + modo.Nome);
                return (200, Json(new { ok = res.Ok, resumo = res.Resumo, modoAtivo = ModeService.ModoAtivoId }));
            }
            return (404, Json(new { ok = false, erro = "rota desconhecida" }));
        }

        private (int, string) Parear(Req r)
        {
            lock (_lockCodigo)
            {
                if (DateTime.UtcNow < _bloqueadoAte) return (429, Json(new { ok = false, erro = "muitas tentativas, aguarde 1 minuto" }));
            }
            if (!FixedEq(Str(r, "code", "codigo").Replace(" ", ""), CodigoPareamento))
            {
                lock (_lockCodigo) { if (++_falhasPar >= 5) { _bloqueadoAte = DateTime.UtcNow.AddMinutes(1); _falhasPar = 0; } }
                return (401, Json(new { ok = false, erro = "código incorreto" }));
            }
            _falhasPar = 0;
            var nome = Str(r, "nome", "name").Trim();
            if (nome.Length == 0) nome = "Celular";
            if (nome.Length > 40) nome = nome.Substring(0, 40);
            var novo = new DispositivoPareado { Nome = nome, Token = ConfigService.NovoToken() };
            ConfigService.Atual.Pareados.Add(novo);
            ConfigService.Salvar(true);
            RegenerarCodigo();     // código vale uma vez só
            CelularPareado?.Invoke(nome);
            Log.Info("Celular pareado: " + nome);
            return (200, Json(new { ok = true, token = novo.Token, pc = Environment.MachineName }));
        }

        private object Status()
        {
            var c = ConfigService.Atual;
            var s = StatsService.Ler();
            var perfil = ConfigService.Perfil;
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
                desfoque = c.DesfoqueWallpaper, escurecer = c.NivelFoscoWallpaper
            };
        }

        private void Marcar(string t) { Atividade?.Invoke(t); Log.Info("Celular → " + t); }

        public void Dispose()
        {
            try { _cts?.Cancel(); _listener?.Stop(); } catch { }
            Rodando = false;
        }
    }
}
