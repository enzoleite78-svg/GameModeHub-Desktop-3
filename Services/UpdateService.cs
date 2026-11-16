using System;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using GameModeHub.Core;

namespace GameModeHub.Services
{
    /// <summary>
    /// NOVO 4.1 (volta da v3, mais seguro): consulta a última release no GitHub.
    /// Não baixa nem substitui nada sozinho — só avisa e abre a página de download.
    /// </summary>
    public static class UpdateService
    {
        public class Resultado
        {
            public bool Ok { get; set; }
            public bool TemNova { get; set; }
            public string Versao { get; set; } = "";
            public string Url { get; set; } = "";
            public string Erro { get; set; } = "";
        }

        public static async Task<Resultado> Verificar()
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("GameModeHub/" + AppInfo.Versao);
                http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
                var json = await http.GetStringAsync($"https://api.github.com/repos/{AppInfo.RepoGitHub}/releases/latest");
                using var doc = JsonDocument.Parse(json);
                var raiz = doc.RootElement;
                var tag = raiz.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
                var url = raiz.TryGetProperty("html_url", out var h) ? h.GetString() : null;
                url ??= $"https://github.com/{AppInfo.RepoGitHub}/releases/latest";
                var nova = ParaVersao(tag); var atual = ParaVersao(AppInfo.Versao);
                return new Resultado { Ok = true, TemNova = nova != null && atual != null && nova > atual, Versao = tag.TrimStart('v', 'V'), Url = url };
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return new Resultado { Ok = true, TemNova = false, Erro = "Ainda não há versões publicadas." };
            }
            catch (Exception ex)
            {
                Log.Warn("Verificar atualização: " + ex.Message);
                return new Resultado { Ok = false, Erro = "Sem conexão com o GitHub agora." };
            }
        }

        private static Version ParaVersao(string s)
        {
            var m = Regex.Match((s ?? "").Trim().TrimStart('v', 'V'), @"^(\d+)(?:\.(\d+))?(?:\.(\d+))?");
            if (!m.Success) return null;
            int P(int i) => m.Groups[i].Success ? int.Parse(m.Groups[i].Value) : 0;
            return new Version(P(1), P(2), P(3));
        }
    }
}
