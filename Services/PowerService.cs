using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using GameModeHub.Core;

namespace GameModeHub.Services
{
    public static class PowerService
    {
        private static readonly Guid Alto = new Guid("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
        private static readonly Guid Maximo = new Guid("e9a42b02-d5df-448d-aa00-03f14749eb61");     // "Desempenho Máximo"
        private static readonly Guid Equilibrado = new Guid("381b4222-f694-41f0-9685-ff5bb260df2e");

        /// <summary>GUID do plano ativo agora (para voltar exatamente a ele ao restaurar).</summary>
        public static string PlanoAtual()
        {
            try
            {
                if (Native.PowerGetActiveScheme(IntPtr.Zero, out IntPtr p) == 0 && p != IntPtr.Zero)
                {
                    var g = Marshal.PtrToStructure<Guid>(p);
                    Native.LocalFree(p);
                    return g.ToString();
                }
            }
            catch (Exception ex) { Log.Warn("Ler plano de energia: " + ex.Message); }
            return "";
        }

        /// <summary>"alto" | "equilibrado" | GUID de um plano | "" (não mexe). Retorna false se o plano não existe neste PC.</summary>
        public static bool DefinirPlano(string plano)
        {
            if (string.IsNullOrEmpty(plano)) return true;
            if (Guid.TryParse(plano, out var direto)) return Aplicar(direto);
            if (!plano.Equals("alto", StringComparison.OrdinalIgnoreCase)) return Aplicar(Equilibrado);

            // CORREÇÃO 4.1: em muitos notebooks/Windows 11 o "Alto desempenho" vem escondido e a troca falhava em silêncio.
            if (Aplicar(Alto) || Aplicar(Maximo)) return true;
            var cfg = ConfigService.Atual;
            if (Guid.TryParse(cfg.PlanoAltoGuid, out var salvo) && Aplicar(salvo)) return true;
            var criado = Duplicar(Alto) ?? Duplicar(Maximo);
            if (criado.HasValue && Aplicar(criado.Value))
            {
                cfg.PlanoAltoGuid = criado.Value.ToString();
                ConfigService.Salvar();
                Log.Info("Plano Alto desempenho recriado: " + cfg.PlanoAltoGuid);
                return true;
            }
            Log.Warn("Plano de alto desempenho indisponível neste PC");
            return false;
        }

        private static bool Aplicar(Guid g)
        {
            try { return Native.PowerSetActiveScheme(IntPtr.Zero, ref g) == 0; }
            catch (Exception ex) { Log.Error("Plano de energia", ex); return false; }
        }

        private static Guid? Duplicar(Guid modelo)
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo("powercfg.exe", "-duplicatescheme " + modelo)
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true });
                var saida = p.StandardOutput.ReadToEnd(); p.WaitForExit(5000);
                foreach (Match m in Regex.Matches(saida, @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}"))
                    if (Guid.TryParse(m.Value, out var g) && g != modelo) return g;
            }
            catch { }
            return null;
        }

        public static void Suspender() => Native.SetSuspendState(false, false, false);
        public static void Desligar() => Executar("/s /t 0");
        public static void Reiniciar() => Executar("/r /t 0");

        private static void Executar(string args)
        {
            try { using var p = Process.Start(new ProcessStartInfo("shutdown.exe", args) { CreateNoWindow = true, UseShellExecute = false }); }
            catch (Exception ex) { Log.Error("shutdown", ex); }
        }
    }
}
