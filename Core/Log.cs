using System;
using System.IO;

namespace GameModeHub.Core
{
    public static class Log
    {
        private static readonly object _lock = new object();
        private const long MaxBytes = 512 * 1024;

        public static void Info(string msg) => Write("INFO", msg);
        public static void Warn(string msg) => Write("WARN", msg);
        // MELHORIA 4.1: o log agora mostra onde o erro aconteceu (3 primeiras linhas da pilha), para facilitar o suporte
        public static void Error(string msg, Exception ex = null) =>
            Write("ERRO", ex == null ? msg : msg + " :: " + ex.GetType().Name + ": " + ex.Message + Pilha(ex));

        private static string Pilha(Exception ex)
        {
            var linhas = (ex.StackTrace ?? "").Split('\n');
            var topo = string.Join(" | ", System.Linq.Enumerable.Take(System.Linq.Enumerable.Select(linhas, l => l.Trim()), 3));
            return topo.Length > 0 ? "  @ " + topo : "";
        }

        private static void Write(string level, string msg)
        {
            try
            {
                lock (_lock)
                {
                    var f = Paths.LogFile;
                    Directory.CreateDirectory(Path.GetDirectoryName(f));
                    var fi = new FileInfo(f);
                    if (fi.Exists && fi.Length > MaxBytes)
                    {
                        var old = f + ".old";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(f, old);
                    }
                    File.AppendAllText(f, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {msg}{Environment.NewLine}");
                }
            }
            catch { /* log nunca pode derrubar o app */ }
        }
    }
}
