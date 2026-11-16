using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GameModeHub.Core;
using Microsoft.Win32;

namespace GameModeHub.Services
{
    /// <summary>
    /// NOVO 4.3: addon EmuSync. Acha (ou baixa) o EmuSync oficial e deixa o GameModeHub conversar com ele.
    /// Os dois continuam programas separados: o EmuSync faz a sincronização, o GameModeHub só mostra e comanda.
    /// </summary>
    public static class EmuSyncAddon
    {
        public const string RepoOficial = "emu-sync/EmuSync";
        public const string NomeServico = "EmuSyncAgent";
        public const string PaginaOficial = "https://github.com/emu-sync/EmuSync";

        public class Deteccao
        {
            public bool ServicoInstalado { get; set; }
            public string CaminhoServico { get; set; } = "";
            public bool ApiOnline { get; set; }
            public bool AppInstalado { get; set; }
            public string CaminhoApp { get; set; } = "";
            public string Versao { get; set; } = "";

            public string Resumo =>
                ApiOnline ? "✔ EmuSync " + (Versao.Length > 0 ? Versao + " " : "") + "encontrado e respondendo"
                : ServicoInstalado ? "⚠ O EmuSync está instalado, mas o serviço dele está parado"
                : AppInstalado ? "⚠ O EmuSync está instalado, mas sem o serviço de sincronização"
                : "O EmuSync não está instalado neste PC";
        }

        /// <summary>Procura o EmuSync neste PC (serviço do Windows + programa instalado) e testa se a API responde.</summary>
        public static async Task<Deteccao> Detectar()
        {
            var d = new Deteccao();
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + NomeServico);
                if (k != null)
                {
                    d.ServicoInstalado = true;
                    d.CaminhoServico = (k.GetValue("ImagePath") as string ?? "").Trim('"').Replace(@"\\", @"\");
                }
            }
            catch (Exception ex) { Log.Warn("EmuSync: ler serviço: " + ex.Message); }

            try
            {
                foreach (var raiz in new[] { Registry.LocalMachine, Registry.CurrentUser })
                foreach (var caminho in new[] { @"Software\Microsoft\Windows\CurrentVersion\Uninstall", @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
                {
                    using var lista = raiz.OpenSubKey(caminho);
                    if (lista == null) continue;
                    foreach (var nome in lista.GetSubKeyNames())
                    {
                        using var app = lista.OpenSubKey(nome);
                        var exibido = app?.GetValue("DisplayName") as string;
                        if (exibido == null || !exibido.StartsWith("EmuSync", StringComparison.OrdinalIgnoreCase)) continue;
                        d.AppInstalado = true;
                        d.Versao = app.GetValue("DisplayVersion") as string ?? "";
                        var pasta = app.GetValue("InstallLocation") as string;
                        if (string.IsNullOrWhiteSpace(pasta))
                        {
                            var desinstalar = (app.GetValue("UninstallString") as string ?? "").Trim();
                            var exe = desinstalar.StartsWith("\"") ? desinstalar.Substring(1, Math.Max(0, desinstalar.IndexOf('"', 1) - 1)) : desinstalar.Split(' ')[0];
                            pasta = Path.GetDirectoryName(exe);
                        }
                        var ui = string.IsNullOrWhiteSpace(pasta) ? null : Path.Combine(pasta, "EmuSync.exe");
                        if (ui != null && File.Exists(ui)) d.CaminhoApp = ui;
                    }
                }
            }
            catch (Exception ex) { Log.Warn("EmuSync: ler programas instalados: " + ex.Message); }

            if (string.IsNullOrEmpty(d.CaminhoApp))
            {
                var padrao = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "EmuSync", "EmuSync.exe");
                if (File.Exists(padrao)) { d.CaminhoApp = padrao; d.AppInstalado = true; }
            }

            d.ApiOnline = await EmuSyncClient.Online();
            return d;
        }

        /// <summary>Liga o serviço do EmuSync (o Windows pede permissão de administrador).</summary>
        public static async Task<bool> IniciarServico()
        {
            try
            {
                var p = Process.Start(new ProcessStartInfo("sc.exe", "start " + NomeServico) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
                if (p != null) await Task.Run(() => p.WaitForExit(15000));
            }
            catch (System.ComponentModel.Win32Exception) { return false; }   // clicou em "Não" no aviso do Windows
            catch (Exception ex) { Log.Error("EmuSync: iniciar serviço", ex); return false; }
            return await EsperarOnline(TimeSpan.FromSeconds(20));
        }

        public static async Task<bool> EsperarOnline(TimeSpan limite, CancellationToken ct = default)
        {
            var fim = DateTime.UtcNow + limite;
            while (DateTime.UtcNow < fim)
            {
                if (await EmuSyncClient.Online()) return true;
                try { await Task.Delay(1500, ct); } catch (TaskCanceledException) { return false; }
            }
            return false;
        }

        public static void AbrirAppOriginal(Deteccao d)
        {
            if (d != null && File.Exists(d.CaminhoApp)) ProcessService.Abrir(d.CaminhoApp);
            else ProcessService.Abrir(PaginaOficial);
        }

        // ================= BAIXAR =================
        public class Instalador
        {
            public string Versao { get; set; } = "";
            public string Url { get; set; } = "";
            public long Tamanho { get; set; }
            public string Nome { get; set; } = "";
        }

        /// <summary>Consulta a última versão no GitHub OFICIAL do EmuSync e acha o instalador do Windows.</summary>
        public static async Task<Instalador> UltimaVersao()
        {
            using var http = NovoHttp(TimeSpan.FromSeconds(15));
            var json = await http.GetStringAsync($"https://api.github.com/repos/{RepoOficial}/releases/latest");
            using var doc = JsonDocument.Parse(json);
            var raiz = doc.RootElement;
            var versao = raiz.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            foreach (var a in raiz.GetProperty("assets").EnumerateArray())
            {
                var nome = a.GetProperty("name").GetString() ?? "";
                var url = a.GetProperty("browser_download_url").GetString() ?? "";
                // só aceita o instalador do Windows, vindo do próprio repositório oficial
                if (!nome.EndsWith("-Win-x64.exe", StringComparison.OrdinalIgnoreCase)) continue;
                if (!url.StartsWith($"https://github.com/{RepoOficial}/releases/download/", StringComparison.OrdinalIgnoreCase)) continue;
                return new Instalador { Versao = versao.TrimStart('v', 'V'), Url = url, Nome = nome, Tamanho = a.GetProperty("size").GetInt64() };
            }
            throw new InvalidOperationException("A última versão do EmuSync não tem instalador para Windows.");
        }

        /// <summary>Baixa o instalador para a pasta temporária. Retorna o caminho do arquivo.</summary>
        public static async Task<string> Baixar(Instalador inst, IProgress<double> progresso, CancellationToken ct)
        {
            var pasta = Path.Combine(Path.GetTempPath(), "GameModeHub");
            Directory.CreateDirectory(pasta);
            var destino = Path.Combine(pasta, Path.GetFileName(inst.Nome));
            var tmp = destino + ".parcial";

            using var http = NovoHttp(TimeSpan.FromMinutes(30));
            using var r = await http.GetAsync(inst.Url, HttpCompletionOption.ResponseHeadersRead, ct);
            r.EnsureSuccessStatusCode();
            long total = r.Content.Headers.ContentLength ?? inst.Tamanho;
            await using (var origem = await r.Content.ReadAsStreamAsync(ct))
            await using (var arq = File.Create(tmp))
            {
                var buf = new byte[81920];
                long lidos = 0; int n;
                while ((n = await origem.ReadAsync(buf, ct)) > 0)
                {
                    await arq.WriteAsync(buf.AsMemory(0, n), ct);
                    lidos += n;
                    if (total > 0) progresso?.Report(lidos * 100.0 / total);
                }
            }
            var tamanho = new FileInfo(tmp).Length;
            if (inst.Tamanho > 0 && tamanho != inst.Tamanho)
            {
                try { File.Delete(tmp); } catch { }
                throw new IOException("O download veio incompleto. Tente de novo.");
            }
            if (File.Exists(destino)) File.Delete(destino);
            File.Move(tmp, destino);
            Log.Info($"EmuSync: instalador baixado ({inst.Versao}, {tamanho / 1048576} MB)");
            return destino;
        }

        /// <summary>Abre o instalador oficial. Ele mesmo pede permissão de administrador e instala o serviço.</summary>
        public static bool AbrirInstalador(string caminho)
        {
            try { Process.Start(new ProcessStartInfo(caminho) { UseShellExecute = true }); return true; }
            catch (System.ComponentModel.Win32Exception) { return false; }
            catch (Exception ex) { Log.Error("EmuSync: abrir instalador", ex); return false; }
        }

        private static HttpClient NovoHttp(TimeSpan tempo)
        {
            var http = new HttpClient { Timeout = tempo };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("GameModeHub/" + AppInfo.Versao);
            return http;
        }

        // ================= INTEGRAÇÕES (modos, controle, celular) =================
        /// <summary>Sincroniza tudo mostrando avisos. Usado pelo controle, bandeja, celular e modos.</summary>
        public static async Task<EmuSyncClient.ResultadoTudo> SincronizarTudoComAviso(string origem)
        {
            if (!ConfigService.Atual.AddonEmuSync) return null;
            if (EmuSyncClient.SincronizandoTudo) { Views.ToastWindow.Mostrar("Saves", "Já estou sincronizando, aguarde.", "🔄"); return null; }
            if (!await EmuSyncClient.Online())
            {
                Views.ToastWindow.Mostrar("EmuSync não respondeu", "O serviço do EmuSync está parado. Veja em Geral → Addons.", "⚠");
                return null;
            }
            Views.ToastWindow.Mostrar("Sincronizando saves…", origem, "🔄");
            try
            {
                var r = await EmuSyncClient.SincronizarTudo();
                Views.ToastWindow.Mostrar(r.Falhas > 0 ? "Saves: alguns deram erro" : "Saves sincronizados", r.Falhas > 0 ? r.Resumo + " — " + r.Erros.First() : r.Resumo, r.Falhas > 0 ? "⚠" : "✔");
                SyncTerminou?.Invoke();
                return r;
            }
            catch (EmuSyncException ex) { Views.ToastWindow.Mostrar("Não consegui sincronizar", ex.Message, "⚠"); return null; }
            catch (Exception ex) { Log.Error("EmuSync: sincronizar tudo", ex); Views.ToastWindow.Mostrar("Não consegui sincronizar", ex.Message, "⚠"); return null; }
        }

        /// <summary>Avisa a tela do EmuSync para recarregar depois de um sync feito por fora (controle, celular, modo).</summary>
        public static event Action SyncTerminou;
        public static void NotificarSync() => SyncTerminou?.Invoke();
    }
}
