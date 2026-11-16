using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using GameModeHub.Core;
using Microsoft.Win32;

namespace GameModeHub.Services
{
    public class JogoInstalado
    {
        public string Id { get; set; } = "";        // steam:570 | epic:Fortnite | gog:1207658930
        public string Nome { get; set; } = "";
        public string Loja { get; set; } = "";      // Steam | Epic | GOG
        public string Capa { get; set; } = "";      // URL da capa (só Steam tem pública)
        internal string Abrir = "";
    }

    /// <summary>NOVO 4.2: lista os jogos instalados (Steam, Epic, GOG) para abrir pelo celular.</summary>
    public static class GameLibraryService
    {
        private static List<JogoInstalado> _cache;
        private static DateTime _cacheEm = DateTime.MinValue;
        private static readonly object _lock = new object();

        // ferramentas da Steam que não são jogos
        private static readonly HashSet<string> IgnorarSteam = new() { "228980", "1070560", "1391110", "1493710", "1628350", "2180100", "250820", "1826330" };

        public static List<JogoInstalado> Listar(bool forcar = false)
        {
            lock (_lock)
            {
                if (!forcar && _cache != null && (DateTime.UtcNow - _cacheEm).TotalSeconds < 60) return _cache;
                var l = new List<JogoInstalado>();
                try { l.AddRange(Steam()); } catch (Exception ex) { Log.Warn("Biblioteca Steam: " + ex.Message); }
                try { l.AddRange(Epic()); } catch (Exception ex) { Log.Warn("Biblioteca Epic: " + ex.Message); }
                try { l.AddRange(Gog()); } catch (Exception ex) { Log.Warn("Biblioteca GOG: " + ex.Message); }
                _cache = l.GroupBy(j => j.Id).Select(g => g.First()).OrderBy(j => j.Nome, StringComparer.OrdinalIgnoreCase).ToList();
                _cacheEm = DateTime.UtcNow;
                return _cache;
            }
        }

        public static bool Abrir(string id)
        {
            var j = Listar().FirstOrDefault(x => x.Id == id) ?? Listar(true).FirstOrDefault(x => x.Id == id);
            if (j == null || string.IsNullOrEmpty(j.Abrir)) return false;
            Log.Info("Abrindo jogo: " + j.Nome);
            ProcessService.Abrir(j.Abrir);
            return true;
        }

        private static IEnumerable<JogoInstalado> Steam()
        {
            var exe = LauncherService.AcharSteam();
            if (exe == null) yield break;
            var raiz = Path.GetDirectoryName(exe);
            var bibliotecas = new List<string> { Path.Combine(raiz, "steamapps") };
            var vdf = Path.Combine(raiz, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf))
                foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                    bibliotecas.Add(Path.Combine(m.Groups[1].Value.Replace("\\\\", "\\"), "steamapps"));
            foreach (var bib in bibliotecas.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(bib)) continue;
                foreach (var acf in Directory.GetFiles(bib, "appmanifest_*.acf"))
                {
                    string txt;
                    try { txt = File.ReadAllText(acf); } catch { continue; }
                    var id = Regex.Match(txt, "\"appid\"\\s+\"(\\d+)\"").Groups[1].Value;
                    var nome = Regex.Match(txt, "\"name\"\\s+\"([^\"]+)\"").Groups[1].Value;
                    if (id.Length == 0 || nome.Length == 0 || IgnorarSteam.Contains(id)) continue;
                    if (nome.IndexOf("Redistributable", StringComparison.OrdinalIgnoreCase) >= 0 || nome.StartsWith("Proton", StringComparison.OrdinalIgnoreCase)
                        || nome.IndexOf("Steam Linux Runtime", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    yield return new JogoInstalado
                    {
                        Id = "steam:" + id, Nome = nome, Loja = "Steam",
                        Capa = $"https://cdn.cloudflare.steamstatic.com/steam/apps/{id}/header.jpg",
                        Abrir = "steam://rungameid/" + id
                    };
                }
            }
        }

        private static IEnumerable<JogoInstalado> Epic()
        {
            var pasta = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");
            if (!Directory.Exists(pasta)) yield break;
            foreach (var arq in Directory.GetFiles(pasta, "*.item"))
            {
                JogoInstalado j = null;
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(arq));
                    var r = doc.RootElement;
                    string S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";
                    if (r.TryGetProperty("bIsIncompleteInstall", out var inc) && inc.ValueKind == JsonValueKind.True) continue;
                    var app = S("AppName"); var ns = S("CatalogNamespace"); var item = S("CatalogItemId"); var nome = S("DisplayName");
                    if (app.Length == 0 || nome.Length == 0) continue;
                    if (r.TryGetProperty("AppCategories", out var cats) && cats.ValueKind == JsonValueKind.Array &&
                        cats.EnumerateArray().Any(c => c.GetString() == "plugins" || c.GetString() == "engines")) continue;
                    j = new JogoInstalado
                    {
                        Id = "epic:" + app, Nome = nome, Loja = "Epic",
                        Abrir = $"com.epicgames.launcher://apps/{ns}%3A{item}%3A{app}?action=launch&silent=true"
                    };
                }
                catch { }
                if (j != null) yield return j;
            }
        }

        private static IEnumerable<JogoInstalado> Gog()
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games");
            if (k == null) yield break;
            foreach (var sub in k.GetSubKeyNames())
            {
                using var g = k.OpenSubKey(sub);
                var nome = g?.GetValue("gameName") as string;
                var exe = g?.GetValue("exe") as string;
                if (string.IsNullOrWhiteSpace(nome) || string.IsNullOrWhiteSpace(exe) || !File.Exists(exe)) continue;
                yield return new JogoInstalado { Id = "gog:" + sub, Nome = nome, Loja = "GOG", Abrir = exe };
            }
        }
    }
}
