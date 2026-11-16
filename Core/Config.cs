using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameModeHub.Core
{
    // ---- Modelos (nomes iguais aos da v3/v4: o config.json antigo continua funcionando) ----
    public class AppConfig
    {
        public int Versao { get; set; } = 4;
        public string Tema { get; set; } = "Dark";                // Dark | OLED | DarkBlue | Light
        public string Accent { get; set; } = "#3DDC84";           // verde
        public bool TintingWallpaperAtivo { get; set; } = true;
        public int NivelFoscoWallpaper { get; set; } = 35;        // "escurecer" 0-100
        public int DesfoqueWallpaper { get; set; } = 40;          // desfoque 0-100
        public string TeclaRestaurar { get; set; } = "NumPad0";
        public string TeclaFecharJogo { get; set; } = "";         // NOVO 4.1: atalho global "fechar jogo travado"
        public string TeclaModoTv { get; set; } = "";             // NOVO 4.1: atalho global "abrir Modo TV"
        public string WallpaperPath { get; set; } = "";
        public string WallpaperFullscreenPath { get; set; } = ""; // papel de parede do Modo TV ("" = usa o mesmo)
        public string EsquemaTeclado { get; set; } = "NumPad";
        public bool IniciarComWindows { get; set; } = false;
        public bool ModoFocoAtivo { get; set; } = true;           // legado v3 (mantido só para compatibilidade)
        public bool SilenciarNotificacoes { get; set; } = false;  // NOVO 4.1: silencia notificações do Windows durante os modos
        public bool IniciarMinimizado { get; set; } = true;
        public bool AbrirModoTvAoGuia { get; set; } = true;
        public bool CombosControle { get; set; } = true;          // NOVO 4.1: LB+RB+... funcionam dentro do jogo
        public bool VibrarControle { get; set; } = true;          // NOVO 4.1
        public bool MostrarAvisos { get; set; } = true;           // NOVO 4.1: avisos na tela ao trocar de modo
        public bool VerificarAtualizacoes { get; set; } = true;   // NOVO 4.1
        public DateTime UltimaVerificacaoUpdate { get; set; } = DateTime.MinValue;
        public bool GuiaInicialOculto { get; set; } = false;
        public bool AvisoBandejaMostrado { get; set; } = false;
        public string RestaurarTelas { get; set; } = "auto";      // auto | todas | manter | modo:<id>
        public string RestaurarAudio { get; set; } = "A";         // A | B | nenhum
        public string PlanoAltoGuid { get; set; } = "";           // cópia do "Alto desempenho" criada quando o Windows esconde o original
        public string PerfilAtivo { get; set; } = "1";
        public string AudioSaidaANome { get; set; } = "Fones / Headset";
        public string AudioSaidaACmd { get; set; } = "";          // id do dispositivo
        public string AudioSaidaBNome { get; set; } = "Caixas do Monitor";
        public string AudioSaidaBCmd { get; set; } = "";
        public string AudioSlotAtivo { get; set; } = "A";
        public int Porta { get; set; } = 5050;
        public List<string> ProgramasIgnorados { get; set; } = new();  // NOVO 4.2: "Ignorar" na varredura da tela Programas
        public bool PermitirVpn { get; set; } = false;           // NOVO 4.2: aceitar celular via Tailscale/ZeroTier (100.64.0.0/10)
        // NOVO 4.3: addon EmuSync (sincronização de saves; o EmuSync oficial roda à parte e o app conversa com ele)
        public bool AddonEmuSync { get; set; } = false;
        public bool EmuSyncComboControle { get; set; } = true;    // LB + RB + Y (1,5 s) sincroniza tudo
        public string Token { get; set; } = "";                   // gerado no 1º uso (atalhos da área de trabalho usam este)
        public List<DispositivoPareado> Pareados { get; set; } = new();
        public Dictionary<string, string> TelasConhecidas { get; set; } = new();
        public Dictionary<string, PerfilModel> Perfis { get; set; } = new();
    }

    public class DispositivoPareado
    {
        public string Nome { get; set; } = "";
        public string Token { get; set; } = "";
        public DateTime Quando { get; set; } = DateTime.Now;
    }

    public class PerfilModel
    {
        public string Nome { get; set; } = "Perfil";
        /// <summary>Modo ativado pelo combo LB + RB + Start do controle.</summary>
        public string ModoIdAtalhoPadrao { get; set; } = "";
        public List<AppFixado> AppsFixados { get; set; } = new();
        public Dictionary<string, bool> AppsMonitorados { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<ModoModel> Modos { get; set; } = new();
    }

    public class AppFixado
    {
        public string Exe { get; set; } = "";
        public bool ManterAberto { get; set; } = false;
    }

    public class ModoModel
    {
        public string Id { get; set; } = "";
        public string Nome { get; set; } = "Novo Modo";
        public string TeclaAtalho { get; set; } = "";
        /// <summary>manter | internal | external | extend | clone | layout:&lt;id&gt;</summary>
        public string Tela { get; set; } = "manter";
        public string UsarSaida { get; set; } = "Nenhum";          // Nenhum | A | B
        public bool Hdr { get; set; } = false;
        public string Launcher { get; set; } = "Steam Big Picture";
        public string LauncherCaminho { get; set; } = "";         // opcional: exe escolhido
        public string Cor { get; set; } = "#3DDC84";
        public string PlanoEnergia { get; set; } = "";             // "" = não mexe; alto | equilibrado
        public bool FecharGentil { get; set; } = true;            // pede para fechar antes de forçar
        public bool LiberarRam { get; set; } = false;             // NOVO 4.1 (volta da v3, agora opcional)
        public bool EsconderCursor { get; set; } = false;         // NOVO 4.1 (volta da v3, agora opcional)
        public override string ToString() => Nome;
    }

    /// <summary>Um layout de telas salvo (ex.: "Só a TV", "2 monitores").</summary>
    public class LayoutTelas
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N").Substring(0, 8);
        public string Nome { get; set; } = "Layout";
        /// <summary>Caminhos estáveis (monitorDevicePath) das telas que devem ficar ACESAS.</summary>
        public List<string> TelasAtivas { get; set; } = new();
        public List<string> NomesAtivos { get; set; } = new();
        public string PrincipalPath { get; set; } = "";
    }

    /// <summary>Formato do arquivo de backup (Geral → Backup).</summary>
    public class BackupModel
    {
        public string Formato { get; set; } = "gamemodehub-backup";
        public string Versao { get; set; } = AppInfo.Versao;
        public DateTime Data { get; set; } = DateTime.Now;
        public AppConfig Config { get; set; }
        public List<LayoutTelas> Layouts { get; set; } = new();
    }

    // ---- Serviço ----
    public static class ConfigService
    {
        private static readonly object _lock = new object();
        public static AppConfig Atual { get; private set; } = new AppConfig();
        public static List<LayoutTelas> Layouts { get; private set; } = new List<LayoutTelas>();

        /// <summary>Disparado quando o perfil ativo muda (pela janela, bandeja ou celular).</summary>
        public static event Action PerfilMudou;

        private static readonly JsonSerializerOptions Opt = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        public static void Carregar()
        {
            Paths.Ensure();
            try
            {
                // migração: se não existe config novo mas existe o da v3 em %APPDATA%, copia.
                if (!File.Exists(Paths.ConfigFile))
                {
                    var antigo = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameModeHub", "config.json");
                    if (File.Exists(antigo) && Paths.Portable) File.Copy(antigo, Paths.ConfigFile, false);
                }
                if (File.Exists(Paths.ConfigFile))
                {
                    var json = File.ReadAllText(Paths.ConfigFile);
                    Atual = JsonSerializer.Deserialize<AppConfig>(json, Opt) ?? new AppConfig();
                }
            }
            catch (Exception ex)
            {
                Log.Error("Config corrompido; tentando o backup", ex);
                try { File.Copy(Paths.ConfigFile, Paths.ConfigFile + ".bad", true); } catch { }
                // CORREÇÃO 4.1: antes voltava tudo ao padrão; agora tenta o .bak que o próprio app grava
                Atual = TentarLer<AppConfig>(Paths.ConfigFile + ".bak") ?? new AppConfig();
            }

            try
            {
                if (File.Exists(Paths.LayoutsFile))
                    Layouts = JsonSerializer.Deserialize<List<LayoutTelas>>(File.ReadAllText(Paths.LayoutsFile), Opt) ?? new();
            }
            catch (Exception ex)
            {
                Log.Error("layouts.json inválido; tentando o backup", ex);
                Layouts = TentarLer<List<LayoutTelas>>(Paths.LayoutsFile + ".bak") ?? new();
            }

            bool semToken = string.IsNullOrWhiteSpace(Atual.Token);
            Normalizar();
            // CORREÇÃO 4.2: o token gerado no 1º uso só ia para o disco quando outra coisa fosse salva;
            // até lá, os atalhos da área de trabalho (--modo) liam outro token e falhavam.
            if (semToken) SalvarAgora();
        }

        private static T TentarLer<T>(string arquivo) where T : class
        {
            try { return File.Exists(arquivo) ? JsonSerializer.Deserialize<T>(File.ReadAllText(arquivo), Opt) : null; }
            catch { return null; }
        }

        /// <summary>Corrige valores nulos/inválidos (config editado à mão, versões antigas, backup de outro PC).</summary>
        private static void Normalizar()
        {
            var c = Atual;
            if (string.IsNullOrWhiteSpace(c.Token)) c.Token = NovoToken();
            c.ProgramasIgnorados ??= new();
            c.Pareados ??= new();
            c.Pareados.RemoveAll(p => p == null || string.IsNullOrWhiteSpace(p.Token));
            c.TelasConhecidas ??= new();
            c.Perfis ??= new();
            foreach (var k in c.Perfis.Where(kv => kv.Value == null).Select(kv => kv.Key).ToList()) c.Perfis.Remove(k);
            if (c.Perfis.Count == 0) c.Perfis["1"] = new PerfilModel { Nome = "Padrão" };
            if (string.IsNullOrEmpty(c.PerfilAtivo) || !c.Perfis.ContainsKey(c.PerfilAtivo)) c.PerfilAtivo = c.Perfis.Keys.First();

            c.Tema ??= "Dark"; c.Accent ??= "#3DDC84";
            c.TeclaRestaurar ??= ""; c.TeclaFecharJogo ??= ""; c.TeclaModoTv ??= "";
            c.WallpaperPath ??= ""; c.WallpaperFullscreenPath ??= "";
            c.AudioSaidaACmd ??= ""; c.AudioSaidaBCmd ??= ""; c.AudioSaidaANome ??= ""; c.AudioSaidaBNome ??= "";
            c.AudioSlotAtivo = c.AudioSlotAtivo == "B" ? "B" : "A";
            c.RestaurarTelas = string.IsNullOrWhiteSpace(c.RestaurarTelas) ? "auto" : c.RestaurarTelas;
            c.RestaurarAudio = c.RestaurarAudio == "B" || c.RestaurarAudio == "nenhum" ? c.RestaurarAudio : "A";
            c.PlanoAltoGuid ??= "";
            if (c.Porta < 1024 || c.Porta > 65535) c.Porta = 5050;

            foreach (var p in c.Perfis.Values)
            {
                p.Nome = string.IsNullOrWhiteSpace(p.Nome) ? "Perfil" : p.Nome;
                p.ModoIdAtalhoPadrao ??= "";
                p.AppsFixados ??= new();
                // CORREÇÃO 4.1: a lista de programas diferenciava "Discord" de "discord"
                var apps = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                if (p.AppsMonitorados != null)
                    foreach (var kv in p.AppsMonitorados)
                        if (!string.IsNullOrWhiteSpace(kv.Key)) apps[kv.Key] = kv.Value;
                p.AppsMonitorados = apps;

                p.Modos ??= new();
                p.Modos.RemoveAll(m => m == null);
                if (p.Modos.Count == 0)
                {
                    p.Modos.Add(new ModoModel { Id = "tv", Nome = "Jogar na TV", Tela = "layout:tv", UsarSaida = "B", Launcher = "Steam Big Picture", Cor = "#F56A0E", TeclaAtalho = "NumPad1" });
                    p.Modos.Add(new ModoModel { Id = "pc", Nome = "Jogar no Monitor", Tela = "layout:pc", UsarSaida = "A", Launcher = "Nenhum", Cor = "#5B9BFF", TeclaAtalho = "NumPad2" });
                }
                var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var m in p.Modos)
                {
                    if (string.IsNullOrEmpty(m.Id) || !ids.Add(m.Id)) { m.Id = NovoId(); ids.Add(m.Id); }
                    m.Nome = string.IsNullOrWhiteSpace(m.Nome) ? "Modo" : m.Nome;
                    m.Tela ??= "manter"; m.UsarSaida ??= "Nenhum"; m.Launcher ??= "Nenhum";
                    m.LauncherCaminho ??= ""; m.TeclaAtalho ??= ""; m.PlanoEnergia ??= "";
                    if (string.IsNullOrWhiteSpace(m.Cor) || !m.Cor.StartsWith("#")) m.Cor = "#3DDC84";
                }
            }

            Layouts ??= new();
            Layouts.RemoveAll(l => l == null);
            foreach (var l in Layouts) { l.TelasAtivas ??= new(); l.NomesAtivos ??= new(); l.PrincipalPath ??= ""; }

            // limite de sanidade nos sliders
            c.DesfoqueWallpaper = Math.Clamp(c.DesfoqueWallpaper, 0, 100);
            c.NivelFoscoWallpaper = Math.Clamp(c.NivelFoscoWallpaper, 0, 100);
        }

        public static PerfilModel Perfil => Atual.Perfis.TryGetValue(Atual.PerfilAtivo, out var p) ? p : Atual.Perfis.Values.First();

        public static string NovoToken()
        {
            var b = new byte[24];
            System.Security.Cryptography.RandomNumberGenerator.Fill(b);
            return Convert.ToBase64String(b).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        }

        public static string NovoId() => Guid.NewGuid().ToString("N").Substring(0, 8);

        private static T Clonar<T>(T o) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(o, Opt), Opt);

        // ================= PERFIS =================
        public static void TrocarPerfil(string chave)
        {
            if (string.IsNullOrEmpty(chave) || !Atual.Perfis.ContainsKey(chave) || chave == Atual.PerfilAtivo) return;
            Atual.PerfilAtivo = chave;
            Salvar(true);
            Log.Info("Perfil ativo: " + Perfil.Nome);
            PerfilMudou?.Invoke();
        }

        /// <summary>Cria um perfil novo copiando o atual (modos, programas e telas). Retorna a chave nova.</summary>
        public static string DuplicarPerfil(string origemChave, string novoNome)
        {
            lock (_lock)
            {
                var origem = Atual.Perfis[origemChave];
                var copia = Clonar(origem);
                copia.Nome = string.IsNullOrWhiteSpace(novoNome) ? origem.Nome + " (cópia)" : novoNome.Trim();
                var mapa = new Dictionary<string, string>();
                foreach (var m in copia.Modos)
                {
                    var novo = NovoId();
                    mapa[m.Id] = novo;
                    if (m.Tela != null && m.Tela.StartsWith("layout:"))
                    {
                        var lay = Layouts.FirstOrDefault(l => l.Id == m.Tela.Substring(7));
                        if (lay != null) { var nl = Clonar(lay); nl.Id = novo; Layouts.Add(nl); m.Tela = "layout:" + novo; }
                    }
                    m.Id = novo;
                }
                if (mapa.TryGetValue(copia.ModoIdAtalhoPadrao ?? "", out var np)) copia.ModoIdAtalhoPadrao = np;
                var chave = NovaChavePerfil();
                Atual.Perfis[chave] = copia;
                Salvar(true);
                return chave;
            }
        }

        public static bool ExcluirPerfil(string chave)
        {
            if (Atual.Perfis.Count <= 1 || !Atual.Perfis.TryGetValue(chave, out var p)) return false;
            lock (_lock)
            {
                Atual.Perfis.Remove(chave);
                // apaga os layouts que só esse perfil usava
                var usados = new HashSet<string>(Atual.Perfis.Values.SelectMany(x => x.Modos).Where(m => m.Tela != null && m.Tela.StartsWith("layout:")).Select(m => m.Tela.Substring(7)));
                foreach (var m in p.Modos) if (!usados.Contains(m.Id)) Layouts.RemoveAll(l => l.Id == m.Id);
                bool eraAtivo = Atual.PerfilAtivo == chave;
                if (eraAtivo) Atual.PerfilAtivo = Atual.Perfis.Keys.First();
                Salvar(true);
                if (eraAtivo) PerfilMudou?.Invoke();
            }
            return true;
        }

        private static string NovaChavePerfil()
        {
            int max = 0;
            foreach (var k in Atual.Perfis.Keys) if (int.TryParse(k, out int n) && n > max) max = n;
            return (max + 1).ToString();
        }

        // ================= BACKUP =================
        public static void ExportarBackup(string caminho)
        {
            string json;
            lock (_lock) json = JsonSerializer.Serialize(new BackupModel { Config = Atual, Layouts = Layouts }, Opt);
            File.WriteAllText(caminho, json);
            Log.Info("Backup exportado: " + caminho);
        }

        /// <summary>Importa um backup (ou um config.json antigo). Mantém pareamentos, token e porta DESTE PC. Retorna null se deu certo, senão a mensagem de erro.</summary>
        public static string ImportarBackup(string caminho)
        {
            try
            {
                var json = File.ReadAllText(caminho);
                AppConfig cfg = null; List<LayoutTelas> lays = null;
                using (var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }))
                {
                    var raiz = doc.RootElement;
                    if (raiz.ValueKind != JsonValueKind.Object) return "Esse arquivo não é um backup do GameModeHub.";
                    JsonElement? cfgEl = null, layEl = null; bool temPerfis = false;
                    foreach (var p in raiz.EnumerateObject())
                    {
                        if (p.Name.Equals("config", StringComparison.OrdinalIgnoreCase)) cfgEl = p.Value;
                        else if (p.Name.Equals("layouts", StringComparison.OrdinalIgnoreCase)) layEl = p.Value;
                        else if (p.Name.Equals("perfis", StringComparison.OrdinalIgnoreCase)) temPerfis = true;
                    }
                    if (cfgEl.HasValue) cfg = cfgEl.Value.Deserialize<AppConfig>(Opt);
                    else if (temPerfis) cfg = raiz.Deserialize<AppConfig>(Opt);       // config.json "cru" (v3/v4)
                    if (layEl.HasValue && layEl.Value.ValueKind == JsonValueKind.Array) lays = layEl.Value.Deserialize<List<LayoutTelas>>(Opt);
                }
                if (cfg == null) return "Esse arquivo não parece ser um backup do GameModeHub.";

                lock (_lock)
                {
                    cfg.Token = Atual.Token; cfg.Pareados = Atual.Pareados; cfg.Porta = Atual.Porta;
                    Atual = cfg;
                    if (lays != null) Layouts = lays;
                    Normalizar();
                }
                SalvarAgora();
                Log.Info("Backup importado: " + caminho);
                PerfilMudou?.Invoke();
                return null;
            }
            catch (Exception ex)
            {
                Log.Error("Importar backup", ex);
                return "Não consegui ler o arquivo: " + ex.Message;
            }
        }

        // ================= SALVAR =================
        // Salva de forma atômica (grava em .tmp e troca) e com atraso (debounce) para sliders.
        private static System.Threading.Timer _debounce;
        public static void Salvar(bool agora = false)
        {
            if (agora) { SalvarAgora(); return; }
            lock (_lock)
            {
                _debounce?.Dispose();
                _debounce = new System.Threading.Timer(_ => SalvarAgora(), null, 600, System.Threading.Timeout.Infinite);
            }
        }

        public static void SalvarAgora()
        {
            lock (_lock)
            {
                _debounce?.Dispose(); _debounce = null;
                for (int tentativa = 0; tentativa < 2; tentativa++)
                {
                    try
                    {
                        Paths.Ensure();
                        var cfg = JsonSerializer.Serialize(Atual, Opt);
                        var lay = JsonSerializer.Serialize(Layouts, Opt);
                        Escrever(Paths.ConfigFile, cfg);
                        Escrever(Paths.LayoutsFile, lay);
                        return;
                    }
                    catch (InvalidOperationException) when (tentativa == 0)
                    {
                        // lista alterada por outra thread durante a gravação: tenta de novo
                        System.Threading.Thread.Sleep(50);
                    }
                    catch (Exception ex) { Log.Error("Falha ao salvar config", ex); return; }
                }
            }
        }

        private static void Escrever(string destino, string conteudo)
        {
            var tmp = destino + ".tmp";
            File.WriteAllText(tmp, conteudo);
            try
            {
                if (File.Exists(destino)) File.Replace(tmp, destino, destino + ".bak", true);
                else File.Move(tmp, destino);
            }
            catch (Exception)
            {
                // CORREÇÃO 4.1: File.Replace falha em alguns pendrives/pastas sincronizadas; cai para cópia simples
                File.Copy(tmp, destino, true);
                try { File.Delete(tmp); } catch { }
            }
        }
    }
}
