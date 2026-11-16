using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using GameModeHub.Core;
using GameModeHub.Server;
using GameModeHub.Services;
using GameModeHub.Views;
using Hardcodet.Wpf.TaskbarNotification;

namespace GameModeHub
{
    public partial class App : Application
    {
        public static ControlServer Servidor { get; private set; }
        public static Beacon Farol { get; private set; }
        public static GamepadService Controle { get; private set; }
        public static HotkeyService Atalhos { get; private set; }
        public static MainWindow Principal { get; private set; }
        public static TvWindow Tv { get; private set; }
        /// <summary>Atalhos que o Windows recusou (já usados por outro programa) no último registro.</summary>
        public static List<string> UltimasFalhasAtalhos { get; private set; } = new();
        public static event Action CelularPareou;

        private Mutex _unica;
        private bool _donoDoMutex;
        private EventWaitHandle _mostrar;
        private static TaskbarIcon _bandeja;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // só uma cópia: se já tem uma aberta, só traz ela para frente (ou repassa o comando do atalho)
            _unica = new Mutex(true, "GameModeHub.Unica", out bool primeira);
            _mostrar = new EventWaitHandle(false, EventResetMode.AutoReset, "GameModeHub.Mostrar");
            if (!primeira && e.Args.Contains("--reaberto"))
            {
                // CORREÇÃO 4.1: "Reabrir como administrador" abria a cópia nova enquanto a antiga ainda estava aberta,
                // e a nova se fechava achando que era duplicada. Agora espera a antiga sair.
                try { primeira = _unica.WaitOne(10000); } catch (AbandonedMutexException) { primeira = true; }
            }
            if (!primeira) { RepassarParaAberto(e.Args); Shutdown(); return; }
            _donoDoMutex = true;
            new Thread(() => { while (_mostrar.WaitOne()) Dispatcher.BeginInvoke(new Action(MostrarPrincipal)); }) { IsBackground = true, Name = "GMH-Mostrar" }.Start();

            DispatcherUnhandledException += (s, a) => { Log.Error("Erro não tratado (UI)", a.Exception); a.Handled = true; };
            AppDomain.CurrentDomain.UnhandledException += (s, a) => Log.Error("Erro fatal", a.ExceptionObject as Exception);
            TaskScheduler.UnobservedTaskException += (s, a) => { Log.Error("Erro em tarefa", a.Exception); a.SetObserved(); };

            ConfigService.Carregar();
            var c = ConfigService.Atual;
            Log.Info($"Iniciado v{AppInfo.Versao} | portátil={Paths.Portable} | admin={SystemService.EhAdmin()} | dados={Paths.DataDir}");
            ThemeService.AplicarTema(c.Tema);
            ThemeService.AplicarAccent(c.Accent);
            ModeService.Inicializar();
            SystemService.SincronizarIniciarComWindows();

            // serviços
            IniciarServidor();
            Farol = new Beacon(); Farol.Iniciar();
            Controle = new GamepadService { VibracaoAtiva = c.VibrarControle };
            // CORREÇÃO 4.1: a opção "abrir Modo TV ao segurar o Guia" existia no config mas era ignorada
            Controle.GuiaSegurado += () => { if (ConfigService.Atual.AbrirModoTvAoGuia) Dispatcher.BeginInvoke(new Action(AbrirTv)); };
            Controle.Combo += cb => { if (ConfigService.Atual.CombosControle) Task.Run(() => ExecutarCombo(cb)); };
            Controle.Iniciar();
            Atalhos = new HotkeyService(); Atalhos.Iniciar();

            ModeService.Aviso += (t, m) => { if (ConfigService.Atual.MostrarAvisos) ToastWindow.Mostrar(t, m); };
            ModeService.ModoAtivoMudou += _ =>
            {
                Controle?.Vibrar(220);
                Dispatcher.BeginInvoke(new Action(MontarMenuBandeja));
            };
            ConfigService.PerfilMudou += () => Dispatcher.BeginInvoke(new Action(() => { RegistrarAtalhos(); MontarMenuBandeja(); }));

            Principal = new MainWindow();
            CriarBandeja();
            var falhas = RegistrarAtalhos();

            bool minimizado = (e.Args.Contains("--minimizado") || e.Args.Contains("--minimized")) && c.IniciarMinimizado;
            if (!minimizado) Principal.Show();
            if (falhas.Count > 0)
                ToastWindow.Mostrar("Alguns atalhos de teclado não funcionaram", "Já usados por outro programa: " + string.Join(", ", falhas), "⚠");

            ExecutarArgumentos(e.Args);
            VerificarAtualizacaoAutomatica();
        }

        // ================= LINHA DE COMANDO (atalhos da área de trabalho) =================
        private static string RotaDosArgumentos(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--modo" && i + 1 < args.Length) return "/modo/" + Uri.EscapeDataString(args[i + 1]);
                if (args[i] == "--restaurar") return "/restaurar";
                if (args[i] == "--tv") return "/tv";
            }
            return null;
        }

        /// <summary>NOVO 4.1: o atalho "GameModeHub - Jogar na TV" da área de trabalho manda o comando para o app já aberto.</summary>
        private void RepassarParaAberto(string[] args)
        {
            var rota = RotaDosArgumentos(args);
            if (rota == null) { _mostrar.Set(); return; }
            try
            {
                ConfigService.Carregar();   // só para ler a porta e o token deste PC
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
                var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{ConfigService.Atual.Porta}{rota}");
                req.Headers.Add("X-GMH-Token", ConfigService.Atual.Token);
                var t = Task.Run(() => http.SendAsync(req));
                if (!t.Wait(5000) || !t.Result.IsSuccessStatusCode) _mostrar.Set();
            }
            catch { _mostrar.Set(); }
        }

        private void ExecutarArgumentos(string[] args)
        {
            var rota = RotaDosArgumentos(args);
            if (rota == null) return;
            Task.Run(async () =>
            {
                await Task.Delay(1500);   // deixa o app terminar de abrir
                if (rota == "/tv") { _ = Dispatcher.BeginInvoke(new Action(AbrirTv)); return; }
                if (rota == "/restaurar") { ModeService.Restaurar(); return; }
                var id = Uri.UnescapeDataString(rota.Substring(6));
                var m = ConfigService.Perfil.Modos.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
                if (m != null) ModeService.Ativar(m.Id);
                else ToastWindow.Mostrar("Modo não encontrado", "O atalho aponta para um modo que não existe mais neste perfil.", "⚠");
            });
        }

        // ================= SERVIDOR =================
        private static void IniciarServidor()
        {
            Servidor = new ControlServer();
            Servidor.CelularPareado += n => { ToastWindow.Mostrar("Celular pareado", n, "📱"); CelularPareou?.Invoke(); };
            Servidor.PedirModoTv += () => Current.Dispatcher.BeginInvoke(new Action(AbrirTv));
            Servidor.Iniciar();
        }

        public static void ReiniciarServidor()
        {
            try { Servidor?.Dispose(); } catch { }
            IniciarServidor();
        }

        // ================= ATALHOS / COMBOS =================
        /// <summary>Registra todos os atalhos globais. Retorna os que o Windows recusou (em uso por outro programa).</summary>
        public static List<string> RegistrarAtalhos()
        {
            var falhas = new List<string>();
            if (Atalhos == null) return falhas;
            Atalhos.LimparTudo();
            var c = ConfigService.Atual;
            void Reg(string tecla, string desc, Action acao)
            {
                if (string.IsNullOrWhiteSpace(tecla)) return;
                if (!Atalhos.Registrar(tecla, acao)) falhas.Add($"{tecla} ({desc})");
            }
            foreach (var m in ConfigService.Perfil.Modos.ToList())
            {
                var id = m.Id;
                Reg(m.TeclaAtalho, m.Nome, () => ModeService.Ativar(id));
            }
            Reg(c.TeclaRestaurar, "Restaurar", () => ModeService.Restaurar());
            Reg(c.TeclaFecharJogo, "Fechar jogo", FecharJogoTravado);
            Reg(c.TeclaModoTv, "Modo TV", () => Current.Dispatcher.BeginInvoke(new Action(AbrirTv)));
            UltimasFalhasAtalhos = falhas;
            return falhas;
        }

        public static void FecharJogoTravado()
        {
            var n = ProcessService.FecharJanelaAtiva();
            ToastWindow.Mostrar(n != null ? "Fechado: " + n : "Nada para fechar", n != null ? "O mouse foi destravado." : "A janela em foco é do sistema ou do próprio GameModeHub.", n != null ? "🛑" : "ℹ");
        }

        private static void ExecutarCombo(ComboControle cb)
        {
            try
            {
                switch (cb)
                {
                    case ComboControle.ModoPadrao:
                        {
                            var p = ConfigService.Perfil;
                            var id = p.Modos.Any(m => m.Id == p.ModoIdAtalhoPadrao) ? p.ModoIdAtalhoPadrao : p.Modos.FirstOrDefault()?.Id;
                            if (id != null) ModeService.Ativar(id);
                            break;
                        }
                    case ComboControle.Restaurar: ModeService.Restaurar(); break;
                    case ComboControle.SincronizarSaves:
                        // NOVO 4.3: LB + RB + Y (1,5 s) — addon EmuSync
                        if (!ConfigService.Atual.AddonEmuSync || !ConfigService.Atual.EmuSyncComboControle) break;
                        Controle?.Vibrar(180);
                        EmuSyncAddon.SincronizarTudoComAviso("Pelo controle").Wait(TimeSpan.FromMinutes(10));
                        break;
                    case ComboControle.FecharJogo: Controle?.Vibrar(300); FecharJogoTravado(); break;
                    case ComboControle.TrocarAudio:
                        {
                            var n = AudioService.Alternar();
                            Controle?.Vibrar(120);
                            ToastWindow.Mostrar("Áudio", n ?? "Saída não encontrada — configure em Geral → Saídas de áudio", n != null ? "🔊" : "⚠");
                            break;
                        }
                    case ComboControle.VolumeMais:
                    case ComboControle.VolumeMenos:
                        {
                            var v = AudioService.LerVolume();
                            if (v < 0) break;
                            v = Math.Clamp(v + (cb == ComboControle.VolumeMais ? 5 : -5), 0, 100);
                            AudioService.DefinirVolume(v);
                            ToastWindow.Mostrar("Volume " + v + "%", null, v == 0 ? "🔈" : "🔊");
                            break;
                        }
                }
            }
            catch (Exception ex) { Log.Error("Combo do controle", ex); }
        }

        // ================= JANELAS =================
        public static void MostrarPrincipal()
        {
            if (Principal == null) return;
            Principal.Show();
            if (Principal.WindowState == WindowState.Minimized) Principal.WindowState = WindowState.Normal;
            Principal.Activate();
            Principal.Topmost = true; Principal.Topmost = false;   // garante que aparece na frente
        }

        public static void AbrirTv()
        {
            if (Tv != null && Tv.IsVisible) { Tv.Activate(); return; }
            Tv = new TvWindow();
            Tv.Show();
            Tv.Activate();
        }

        // ================= BANDEJA =================
        private void CriarBandeja()
        {
            try
            {
                var info = GetResourceStream(new Uri("pack://application:,,,/icone.ico"));
                _bandeja = new TaskbarIcon
                {
                    Icon = new Icon(info.Stream),
                    ToolTipText = "GameModeHub",
                    ContextMenu = new ContextMenu()
                };
                _bandeja.TrayMouseDoubleClick += (s, a) => MostrarPrincipal();
                _bandeja.TrayLeftMouseUp += (s, a) => MostrarPrincipal();     // MELHORIA 4.1: um clique já abre
                MontarMenuBandeja();
            }
            catch (Exception ex) { Log.Error("Bandeja", ex); }
        }

        /// <summary>MELHORIA 4.1: o menu da bandeja agora lista os modos (ativar com 1 clique) e mostra o modo atual.</summary>
        private static void MontarMenuBandeja()
        {
            if (_bandeja?.ContextMenu == null) return;
            var menu = _bandeja.ContextMenu;
            menu.Items.Clear();
            var perfil = ConfigService.Perfil;
            var ativo = perfil.Modos.FirstOrDefault(m => m.Id == ModeService.ModoAtivoId);
            _bandeja.ToolTipText = "GameModeHub · " + (ativo != null ? "Modo: " + ativo.Nome : "Desktop normal");

            menu.Items.Add(new MenuItem { Header = ativo != null ? "● " + ativo.Nome : "Desktop normal", IsEnabled = false });
            menu.Items.Add(new Separator());

            var modos = new MenuItem { Header = "🎮   Ativar modo" };
            foreach (var m in perfil.Modos)
            {
                var id = m.Id;
                var mi = new MenuItem { Header = m.Nome + (string.IsNullOrWhiteSpace(m.TeclaAtalho) ? "" : "   (" + m.TeclaAtalho + ")"), IsChecked = m.Id == ModeService.ModoAtivoId };
                mi.Click += (s, a) => Task.Run(() => ModeService.Ativar(id));
                modos.Items.Add(mi);
            }
            if (perfil.Modos.Count == 0) modos.Items.Add(new MenuItem { Header = "Nenhum modo neste perfil", IsEnabled = false });
            menu.Items.Add(modos);

            if (ConfigService.Atual.Perfis.Count > 1)
            {
                var perfis = new MenuItem { Header = "👤   Perfil: " + perfil.Nome };
                foreach (var kv in ConfigService.Atual.Perfis)
                {
                    var chave = kv.Key;
                    var mi = new MenuItem { Header = kv.Value.Nome, IsChecked = chave == ConfigService.Atual.PerfilAtivo };
                    mi.Click += (s, a) => ConfigService.TrocarPerfil(chave);
                    perfis.Items.Add(mi);
                }
                menu.Items.Add(perfis);
            }

            void Item(string texto, Action acao) { var mi = new MenuItem { Header = texto }; mi.Click += (s, a) => acao(); menu.Items.Add(mi); }
            Item("↩   Restaurar desktop", () => Task.Run(() => ModeService.Restaurar()));
            Item("🔊   Trocar áudio (A ⇄ B)", () => Task.Run(() =>
            {
                var n = AudioService.Alternar();
                ToastWindow.Mostrar("Áudio", n ?? "Saída não encontrada — configure em Geral → Saídas de áudio", n != null ? "🔊" : "⚠");
            }));
            if (ConfigService.Atual.AddonEmuSync)   // NOVO 4.3
                Item("🔄   Sincronizar saves (EmuSync)", () => Task.Run(() => EmuSyncAddon.SincronizarTudoComAviso("Pela bandeja")));
            Item("📺   Modo TV (tela cheia)", AbrirTv);
            Item("💡   Acender todas as telas", () => Task.Run(() => { var r = DisplayService.AcenderTodas(); ToastWindow.Mostrar(r.ok ? "Todas as telas acesas" : "Não consegui", r.msg, r.ok ? "💡" : "⚠"); }));
            menu.Items.Add(new Separator());
            Item("Abrir GameModeHub", MostrarPrincipal);
            Item("Sair", Sair);
        }

        /// <summary>NOVO 4.3: um addon foi ativado/desativado — atualiza o que depende dele (menu da bandeja).</summary>
        public static void AddonsMudaram() => Current?.Dispatcher.BeginInvoke(new Action(MontarMenuBandeja));

        public static void Sair()
        {
            try { ConfigService.SalvarAgora(); } catch { }
            Principal?.PermitirFechar();
            Current.Shutdown();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                Servidor?.Dispose(); Farol?.Dispose(); Controle?.Dispose(); Atalhos?.Dispose();
                _bandeja?.Dispose();
                if (_donoDoMutex) _unica?.ReleaseMutex();
            }
            catch { }
            base.OnExit(e);
        }

        // ================= ATUALIZAÇÕES =================
        private void VerificarAtualizacaoAutomatica()
        {
            var c = ConfigService.Atual;
            if (!c.VerificarAtualizacoes || (DateTime.Now - c.UltimaVerificacaoUpdate).TotalHours < 20) return;
            Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(12));
                var r = await UpdateService.Verificar();
                if (!r.Ok) return;
                ConfigService.Atual.UltimaVerificacaoUpdate = DateTime.Now; ConfigService.Salvar();
                if (!r.TemNova) return;
                Log.Info("Nova versão disponível: " + r.Versao);
                _ = Current.Dispatcher.BeginInvoke(new Action(() => Principal?.MostrarResultadoUpdate(r, false)));
                ToastWindow.Mostrar("Nova versão " + r.Versao + " disponível", "Veja em Geral → Atualizações.", "🎉");
            });
        }
    }
}
