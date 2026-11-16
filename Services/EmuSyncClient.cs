using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using GameModeHub.Core;

namespace GameModeHub.Services
{
    // ---- Modelos da API local do EmuSync (EmuSync.Agent, http://localhost:5353) ----
    // Os nomes seguem os DTOs do EmuSync 1.0.x. Campos que não usamos são ignorados.

    public enum EsStatus { Desconhecido = 0, PrecisaBaixar = 1, PrecisaEnviar = 2, EmDia = 3, SemPasta = 4 }

    public enum EsProvedor { Nenhum = 0, GoogleDrive = 1, Dropbox = 2, OneDrive = 3, PastaCompartilhada = 4 }

    public class EsJogo
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("name")] public string Nome { get; set; } = "";
        [JsonPropertyName("autoSync")] public bool AutoSync { get; set; }
        [JsonPropertyName("maximumLocalGameBackups")] public int? MaxBackups { get; set; }
        [JsonPropertyName("syncSourceIdLocations")] public Dictionary<string, string> Pastas { get; set; } = new();
        [JsonPropertyName("lastSyncedFrom")] public string UltimoSyncDe { get; set; }
        [JsonPropertyName("lastSyncTimeUtc")] public DateTime? UltimoSyncUtc { get; set; }
        [JsonPropertyName("syncStatusId")] public int StatusId { get; set; }
        [JsonPropertyName("storageBytes")] public long? Bytes { get; set; }

        [JsonIgnore] public EsStatus Status => Enum.IsDefined(typeof(EsStatus), StatusId) ? (EsStatus)StatusId : EsStatus.Desconhecido;
        public string PastaDe(string aparelhoId) => aparelhoId != null && Pastas != null && Pastas.TryGetValue(aparelhoId, out var p) ? p : null;
    }

    public class EsStatusJogo
    {
        [JsonPropertyName("lastSyncedFrom")] public string UltimoSyncDe { get; set; }
        [JsonPropertyName("lastSyncedAtUtc")] public DateTime? UltimoSyncUtc { get; set; }
        [JsonPropertyName("latestWriteTimeUtc")] public DateTime? NuvemAlteradoUtc { get; set; }
        [JsonPropertyName("localLatestWriteTimeUtc")] public DateTime? LocalAlteradoUtc { get; set; }
        [JsonPropertyName("requiresUpload")] public bool PrecisaEnviar { get; set; }
        [JsonPropertyName("requiresDownload")] public bool PrecisaBaixar { get; set; }
        [JsonPropertyName("localFolderPathIsUnset")] public bool SemPasta { get; set; }
        [JsonPropertyName("localFolderPathExists")] public bool PastaExiste { get; set; }
        [JsonPropertyName("storageBytes")] public long? Bytes { get; set; }
    }

    public class EsAparelho
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("name")] public string Nome { get; set; } = "";
        [JsonPropertyName("storageProviderId")] public int? ProvedorId { get; set; }
        [JsonPropertyName("platformId")] public int PlataformaId { get; set; }
        [JsonPropertyName("autoSyncFrequencyMins")] public int FrequenciaMin { get; set; }
        [JsonPropertyName("maximumLocalGameBackups")] public int MaxBackups { get; set; }

        [JsonIgnore] public EsProvedor Provedor => ProvedorId is int p && Enum.IsDefined(typeof(EsProvedor), p) ? (EsProvedor)p : EsProvedor.Nenhum;
        [JsonIgnore] public string Plataforma => PlataformaId switch { 1 => "Windows", 2 => "Linux / Android", 3 => "Mac", _ => "?" };
        [JsonIgnore] public string Icone => Nome.StartsWith("Android", StringComparison.OrdinalIgnoreCase) ? "📱"
                                         : Nome.IndexOf("deck", StringComparison.OrdinalIgnoreCase) >= 0 ? "🎮"
                                         : PlataformaId == 2 ? "🐧" : PlataformaId == 3 ? "🍎" : "💻";
    }

    public class EsBackup
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("backupFileName")] public string Arquivo { get; set; } = "";
        [JsonPropertyName("createdOnUtc")] public DateTime CriadoUtc { get; set; }
    }

    public class EsSugestao
    {
        [JsonPropertyName("name")] public string Nome { get; set; } = "";
        [JsonPropertyName("suggestedFolderPaths")] public List<string> Pastas { get; set; } = new();
        public override string ToString() => Nome;
    }

    public class EsProgresso
    {
        [JsonPropertyName("inProgress")] public bool EmAndamento { get; set; }
        [JsonPropertyName("overallCompletionPercent")] public double? Percentual { get; set; }
        [JsonPropertyName("currentStage")] public string Etapa { get; set; }
    }

    public class EsLog
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("gameId")] public string JogoId { get; set; } = "";
        [JsonPropertyName("isAutoSync")] public bool Automatico { get; set; }
        [JsonPropertyName("syncTimeUtc")] public DateTime QuandoUtc { get; set; }
        [JsonPropertyName("syncType")] public int Tipo { get; set; }      // 1 = envio, 2 = download
    }

    public class EsVarredura
    {
        [JsonPropertyName("lastScanSeconds")] public double UltimaHaSegundos { get; set; }
        [JsonPropertyName("inProgress")] public bool EmAndamento { get; set; }
        [JsonPropertyName("progressPercent")] public double Percentual { get; set; }
        [JsonPropertyName("countOfGames")] public int JogosEncontrados { get; set; }
    }

    /// <summary>Erro devolvido pelo EmuSync, já com uma mensagem legível.</summary>
    public class EmuSyncException : Exception
    {
        public int Codigo { get; }
        public EmuSyncException(int codigo, string msg) : base(msg) { Codigo = codigo; }
    }

    /// <summary>
    /// Cliente da API local do EmuSync. O EmuSync instala um serviço do Windows ("EmuSyncAgent") que faz a sincronização;
    /// a janela original do EmuSync só conversa com ele por HTTP em localhost:5353. Aqui fazemos o mesmo.
    /// Nada sai do PC: a API só escuta em localhost.
    /// </summary>
    public static class EmuSyncClient
    {
        public const string Base = "http://localhost:5353";

        private static readonly HttpClient Http = new HttpClient { BaseAddress = new Uri(Base), Timeout = Timeout.InfiniteTimeSpan };

        private static readonly JsonSerializerOptions Opt = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        // tempos: leituras rápidas; sync pode demorar (download/upload de saves grandes)
        private static readonly TimeSpan Rapido = TimeSpan.FromSeconds(8);
        private static readonly TimeSpan Normal = TimeSpan.FromSeconds(40);
        private static readonly TimeSpan Longo = TimeSpan.FromMinutes(15);

        // ================= BÁSICO =================
        public static async Task<bool> Online(int ms = 2500)
        {
            try
            {
                using var cts = new CancellationTokenSource(ms);
                using var r = await Http.GetAsync("/System/HealthCheck", cts.Token);
                return r.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        private static async Task<string> Enviar(HttpMethod metodo, string rota, object corpo, TimeSpan tempo)
        {
            using var cts = new CancellationTokenSource(tempo);
            using var req = new HttpRequestMessage(metodo, rota);
            if (corpo != null) req.Content = new StringContent(JsonSerializer.Serialize(corpo, Opt), Encoding.UTF8, "application/json");
            HttpResponseMessage r;
            try { r = await Http.SendAsync(req, cts.Token); }
            catch (TaskCanceledException) { throw new EmuSyncException(0, "O EmuSync demorou demais para responder."); }
            catch (HttpRequestException) { throw new EmuSyncException(0, "O serviço do EmuSync não está respondendo. Veja em Geral → Addons."); }
            using (r)
            {
                var txt = await r.Content.ReadAsStringAsync();
                if (r.IsSuccessStatusCode) return txt;
                throw new EmuSyncException((int)r.StatusCode, MensagemDeErro((int)r.StatusCode, txt));
            }
        }

        private static string MensagemDeErro(int codigo, string corpo)
        {
            try
            {
                using var doc = JsonDocument.Parse(corpo);
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Array)
                {
                    var lista = errs.EnumerateArray().Select(e => e.GetString()).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
                    if (lista.Count > 0) return Traduzir(string.Join(" ", lista));
                }
            }
            catch { }
            if (!string.IsNullOrWhiteSpace(corpo) && corpo.Length < 300 && !corpo.TrimStart().StartsWith("{")) return Traduzir(corpo.Trim('"'));
            return codigo switch
            {
                404 => "Não encontrado no EmuSync (talvez tenha sido apagado em outro aparelho).",
                500 => "O EmuSync teve um erro interno. Veja o log do EmuSync.",
                _ => "O EmuSync recusou o pedido (código " + codigo + ")."
            };
        }

        /// <summary>As mensagens do EmuSync vêm em inglês; traduz as mais comuns.</summary>
        private static string Traduzir(string m)
        {
            if (m.Contains("No sync source", StringComparison.OrdinalIgnoreCase)) return "O EmuSync ainda não está conectado a uma nuvem (Google Drive, Dropbox…). Conecte na aba Configurar.";
            if (m.Contains("No game found", StringComparison.OrdinalIgnoreCase)) return "Esse jogo não existe mais no EmuSync.";
            if (m.Contains("There was an error on the server", StringComparison.OrdinalIgnoreCase)) return "O EmuSync teve um erro ao fazer isso. Veja o log do EmuSync.";
            return m;
        }

        private static async Task<T> Get<T>(string rota, TimeSpan? tempo = null)
        {
            var txt = await Enviar(HttpMethod.Get, rota, null, tempo ?? Normal);
            return string.IsNullOrWhiteSpace(txt) ? default : JsonSerializer.Deserialize<T>(txt, Opt);
        }

        private static string Id(string id) => Uri.EscapeDataString(id ?? "");

        // ================= APARELHOS / NUVEM =================
        /// <summary>Este PC (cria o registro na primeira vez, como o EmuSync original faz).</summary>
        public static Task<EsAparelho> Local() => Get<EsAparelho>("/SyncSource/Local");
        public static async Task<List<EsAparelho>> Aparelhos() => await Get<List<EsAparelho>>("/SyncSource") ?? new();
        public static Task SalvarLocal(string nome, int frequenciaMin, int maxBackups) =>
            Enviar(HttpMethod.Put, "/SyncSource/Local", new { name = nome, autoSyncFrequencyMins = frequenciaMin, maximumLocalGameBackups = maxBackups }, Normal);
        public static Task ExcluirAparelho(string id) => Enviar(HttpMethod.Delete, "/SyncSource/" + Id(id), null, Normal);
        public static Task Desconectar(bool forcar = false) => Enviar(HttpMethod.Delete, "/SyncSource/Local/StorageProvider" + (forcar ? "?force=true" : ""), null, Normal);
        public static async Task<double> SegundosParaProximoAuto() => (await Get<JsonElement>("/SyncSource/NextAutoSyncTime", Rapido)).GetProperty("secondsLeft").GetDouble();
        public static Task<EsVarredura> Varredura() => Get<EsVarredura>("/SyncSource/GameScanDetails", Rapido);
        public static Task ForcarVarredura() => Enviar(HttpMethod.Post, "/SyncSource/ForceGameScan", null, Rapido);

        /// <summary>Endereço de login no navegador. Depois do login, o próprio EmuSync recebe o retorno em localhost:5353.</summary>
        public static async Task<string> UrlLogin(EsProvedor p)
        {
            var rota = p switch
            {
                EsProvedor.GoogleDrive => "/Auth/Google/AuthUrl",
                EsProvedor.Dropbox => "/Auth/Dropbox/AuthUrl",
                EsProvedor.OneDrive => "/Auth/Microsoft/AuthUrl",
                _ => throw new ArgumentException("provedor sem login")
            };
            return (await Get<JsonElement>(rota)).GetProperty("url").GetString();
        }

        public static Task ConectarPastaCompartilhada(string caminho, string usuario, string senha) =>
            Enviar(HttpMethod.Post, "/Auth/SharedFolder/AuthFinish", new { path = caminho, username = NuloSeVazio(usuario), password = NuloSeVazio(senha) }, Normal);

        private static string NuloSeVazio(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

        // ================= JOGOS =================
        public static async Task<List<EsJogo>> Jogos() => await Get<List<EsJogo>>("/Game", Normal) ?? new();
        public static Task<EsJogo> Jogo(string id) => Get<EsJogo>("/Game/" + Id(id));
        public static async Task<List<EsSugestao>> Sugestoes() => await Get<List<EsSugestao>>("/Game/Suggestions") ?? new();
        public static async Task<List<EsBackup>> Backups(string id) => await Get<List<EsBackup>>("/Game/" + Id(id) + "/Backups") ?? new();

        public static async Task<EsJogo> CriarJogo(string nome, bool auto, Dictionary<string, string> pastas, int? maxBackups)
        {
            var txt = await Enviar(HttpMethod.Post, "/Game", new { name = nome, autoSync = auto, syncSourceIdLocations = pastas, maximumLocalGameBackups = maxBackups }, Normal);
            return JsonSerializer.Deserialize<EsJogo>(txt, Opt);
        }

        public static Task SalvarJogo(string id, string nome, bool auto, Dictionary<string, string> pastas, int? maxBackups) =>
            Enviar(HttpMethod.Put, "/Game/" + Id(id), new { id, name = nome, autoSync = auto, syncSourceIdLocations = pastas, maximumLocalGameBackups = maxBackups }, Normal);

        public static Task ExcluirJogo(string id) => Enviar(HttpMethod.Delete, "/Game/" + Id(id), null, Normal);
        public static Task LimparCache() => Enviar(HttpMethod.Post, "/Game/ClearCache", null, Rapido);

        // ================= SYNC =================
        public static Task<EsStatusJogo> StatusJogo(string id) => Get<EsStatusJogo>("/GameSync/" + Id(id));
        public static Task<EsProgresso> Progresso(string id) => Get<EsProgresso>("/GameSync/" + Id(id) + "/SyncProgress", Rapido);
        public static Task Sincronizar(string id) => Enviar(HttpMethod.Post, "/GameSync/" + Id(id), null, Longo);
        public static Task ForcarEnvio(string id) => Enviar(HttpMethod.Post, "/GameSync/" + Id(id) + "/ForceUpload", null, Longo);
        public static Task ForcarDownload(string id) => Enviar(HttpMethod.Post, "/GameSync/" + Id(id) + "/ForceDownload", null, Longo);
        public static Task RestaurarBackup(string id, string backupId) => Enviar(HttpMethod.Post, "/GameSync/" + Id(id) + "/RestoreFromBackup/" + Id(backupId), null, Longo);
        public static Task ExcluirBackup(string id, string backupId) => Enviar(HttpMethod.Delete, "/GameSync/" + Id(id) + "/Backup/" + Id(backupId), null, Normal);

        public static async Task<List<EsLog>> Historico(string jogoId = null) =>
            await Get<List<EsLog>>(jogoId == null ? "/LocalSyncLog" : "/LocalSyncLog/Game/" + Id(jogoId)) ?? new();

        // ================= SINCRONIZAR TUDO =================
        public class ResultadoTudo
        {
            public int Enviados, Baixados, EmDia, SemPasta, Falhas;
            public List<string> Erros { get; } = new();
            public string Resumo
            {
                get
                {
                    var p = new List<string>();
                    if (Enviados > 0) p.Add($"{Enviados} enviado(s)");
                    if (Baixados > 0) p.Add($"{Baixados} baixado(s)");
                    if (Falhas > 0) p.Add($"{Falhas} com erro");
                    if (p.Count == 0) p.Add(EmDia > 0 ? "Tudo em dia" : "Nenhum jogo com pasta neste PC");
                    return string.Join(" • ", p);
                }
            }
        }

        private static readonly SemaphoreSlim _travaTudo = new SemaphoreSlim(1, 1);
        public static bool SincronizandoTudo => _travaTudo.CurrentCount == 0;

        /// <summary>
        /// Sincroniza todos os jogos que têm pasta neste PC e que estão diferentes da nuvem.
        /// Usa a mesma regra do EmuSync (o mais novo vence) — é o mesmo que clicar em "Sync" em cada jogo.
        /// </summary>
        public static async Task<ResultadoTudo> SincronizarTudo(Action<string> progresso = null, CancellationToken ct = default)
        {
            var r = new ResultadoTudo();
            if (!await _travaTudo.WaitAsync(0, ct)) { r.Erros.Add("Já estou sincronizando, aguarde."); r.Falhas++; return r; }
            try
            {
                var local = await Local();
                if (local.Provedor == EsProvedor.Nenhum) throw new EmuSyncException(400, "O EmuSync ainda não está conectado a uma nuvem. Conecte na aba Configurar.");
                var jogos = await Jogos();
                foreach (var j in jogos)
                {
                    ct.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(j.PastaDe(local.Id))) { r.SemPasta++; continue; }
                    try
                    {
                        var st = await StatusJogo(j.Id);
                        if (st.SemPasta || !st.PastaExiste && !st.PrecisaBaixar) { r.SemPasta++; continue; }
                        if (!st.PrecisaEnviar && !st.PrecisaBaixar) { r.EmDia++; continue; }
                        progresso?.Invoke((st.PrecisaEnviar ? "Enviando " : "Baixando ") + j.Nome + "…");
                        await Sincronizar(j.Id);
                        if (st.PrecisaEnviar) r.Enviados++; else r.Baixados++;
                    }
                    catch (EmuSyncException ex) { r.Falhas++; r.Erros.Add(j.Nome + ": " + ex.Message); Log.Warn("EmuSync " + j.Nome + ": " + ex.Message); }
                }
                Log.Info("EmuSync: sincronizar tudo → " + r.Resumo);
                return r;
            }
            finally { _travaTudo.Release(); }
        }
    }
}
