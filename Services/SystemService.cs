using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Principal;
using System.Text;
using GameModeHub.Core;
using Microsoft.Win32;

namespace GameModeHub.Services
{
    /// <summary>Firewall, iniciar com o Windows, atalhos na área de trabalho, notificações, modo administrador opcional.</summary>
    public static class SystemService
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string PushKey = @"Software\Microsoft\Windows\CurrentVersion\PushNotifications";
        private const string RegraLegada = "GameModeHub";

        public static bool EhAdmin()
        {
            try { using var id = WindowsIdentity.GetCurrent(); return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator); }
            catch { return false; }
        }

        // ================= INICIAR COM O WINDOWS =================
        public static void DefinirIniciarComWindows(bool ligar)
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(RunKey, true);
                if (k == null) return;
                if (ligar) k.SetValue("GameModeHub", "\"" + Environment.ProcessPath + "\" --minimizado");
                else k.DeleteValue("GameModeHub", false);
            }
            catch (Exception ex) { Log.Error("Iniciar com Windows", ex); }
        }

        public static bool IniciaComWindows()
        {
            try { using var k = Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue("GameModeHub") is string; }
            catch { return false; }
        }

        /// <summary>
        /// CORREÇÃO 4.1: na versão portátil, se você movesse a pasta, o "Iniciar com o Windows" apontava para o lugar antigo.
        /// Agora o caminho é atualizado toda vez que o app abre.
        /// </summary>
        public static void SincronizarIniciarComWindows()
        {
            if (ConfigService.Atual.IniciarComWindows) DefinirIniciarComWindows(true);
        }

        // ================= FIREWALL =================
        private static string RegraTcp(int porta) => $"GameModeHub TCP {porta}";
        private const string RegraUdp = "GameModeHub UDP 5051";

        public static bool FirewallLiberado(int porta) => RegraExiste(RegraTcp(porta));

        private static bool RegraExiste(string nome)
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo("netsh", $"advfirewall firewall show rule name=\"{nome}\"")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true });
                var o = p.StandardOutput.ReadToEnd(); p.WaitForExit(4000);
                return p.ExitCode == 0 && o.IndexOf(nome, StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }

        /// <summary>
        /// Pede permissão de administrador UMA vez só para criar as regras. O app em si continua sem admin.
        /// CORREÇÃO 4.1:
        ///  • a regra antiga só valia em rede "Particular" — em rede "Pública" (padrão depois de formatar) o celular não conectava;
        ///    agora vale em qualquer perfil, mas SÓ para aparelhos da mesma sub-rede (LocalSubnet);
        ///  • remove bloqueios que o Windows cria quando alguém clica "Cancelar" no aviso de firewall;
        ///  • a regra leva a porta no nome: se você trocar a porta, o app percebe que precisa liberar de novo.
        /// </summary>
        public static bool LiberarFirewall(int porta)
        {
            try
            {
                var exe = (Environment.ProcessPath ?? "").Replace("'", "''");
                var ps = new StringBuilder();
                ps.AppendLine("$ErrorActionPreference = 'SilentlyContinue'");
                ps.AppendLine("Remove-NetFirewallRule -DisplayName 'GameModeHub*'");
                if (exe.Length > 0) ps.AppendLine($"Get-NetFirewallApplicationFilter -Program '{exe}' | Get-NetFirewallRule | Remove-NetFirewallRule");
                ps.AppendLine($"New-NetFirewallRule -DisplayName '{RegraTcp(porta)}' -Direction Inbound -Action Allow -Protocol TCP -LocalPort {porta} -RemoteAddress LocalSubnet -Profile Any | Out-Null");
                ps.AppendLine($"New-NetFirewallRule -DisplayName '{RegraUdp}' -Direction Inbound -Action Allow -Protocol UDP -LocalPort 5051 -RemoteAddress LocalSubnet -Profile Any | Out-Null");
                var cod = Convert.ToBase64String(Encoding.Unicode.GetBytes(ps.ToString()));
                using var p = Process.Start(new ProcessStartInfo("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand " + cod)
                { Verb = "runas", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden });
                p?.WaitForExit(30000);
                var ok = FirewallLiberado(porta);
                Log.Info("Firewall liberado: " + ok);
                return ok;
            }
            catch (Exception ex) { Log.Warn("Firewall: " + ex.Message); return false; }   // usuário negou o UAC
        }

        // ================= NOTIFICAÇÕES (beta) =================
        /// <summary>1 = notificações ligadas, 0 = desligadas.</summary>
        public static int LerNotificacoes()
        {
            try { using var k = Registry.CurrentUser.OpenSubKey(PushKey); return k?.GetValue("ToastEnabled") is int v ? v : 1; }
            catch { return 1; }
        }

        public static void DefinirNotificacoes(int valor)
        {
            try
            {
                using var k = Registry.CurrentUser.CreateSubKey(PushKey);
                k?.SetValue("ToastEnabled", valor == 0 ? 0 : 1, RegistryValueKind.DWord);
                Native.SendMessageTimeout(Native.HWND_BROADCAST, Native.WM_SETTINGCHANGE, IntPtr.Zero, "Notifications", Native.SMTO_ABORTIFHUNG, 500, out _);
                Log.Info("Notificações do Windows: " + (valor == 0 ? "silenciadas" : "ligadas"));
            }
            catch (Exception ex) { Log.Warn("Notificações: " + ex.Message); }
        }

        // ================= ADMIN =================
        /// <summary>Reinicia o app como administrador (opcional: só se um jogo/app protegido precisar).</summary>
        public static void ReiniciarComoAdmin()
        {
            try
            {
                Process.Start(new ProcessStartInfo(Environment.ProcessPath) { Verb = "runas", UseShellExecute = true, Arguments = "--reaberto" });
                App.Sair();
            }
            catch (Exception ex) { Log.Warn("Admin negado: " + ex.Message); }
        }

        // ================= ATALHO NA ÁREA DE TRABALHO =================
        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        private class ShellLink { }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
        private interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, int fFlags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            void GetHotkey(out short pwHotkey);
            void SetHotkey(short wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
            void Resolve(IntPtr hwnd, int fFlags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }

        /// <summary>NOVO 4.1: cria um atalho na área de trabalho que ativa um modo com dois cliques.</summary>
        public static string CriarAtalhoAreaDeTrabalho(string nome, string argumentos, string descricao)
        {
            try
            {
                var exe = Environment.ProcessPath;
                var seguro = string.Concat(nome.Split(Path.GetInvalidFileNameChars())).Trim();
                if (seguro.Length == 0) seguro = "GameModeHub";
                var destino = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), seguro + ".lnk");
                var link = (IShellLinkW)new ShellLink();
                link.SetPath(exe);
                link.SetArguments(argumentos);
                link.SetWorkingDirectory(Path.GetDirectoryName(exe));
                link.SetDescription(descricao);
                link.SetIconLocation(exe, 0);
                ((IPersistFile)link).Save(destino, true);
                Marshal.FinalReleaseComObject(link);
                Log.Info("Atalho criado: " + destino);
                return destino;
            }
            catch (Exception ex) { Log.Error("Criar atalho", ex); return null; }
        }
    }
}
