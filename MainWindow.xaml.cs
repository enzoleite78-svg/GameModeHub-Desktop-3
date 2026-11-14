using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GameModeHub.Core;
using GameModeHub.Server;
using GameModeHub.Services;
using GameModeHub.Views;
using QRCoder;

namespace GameModeHub
{
    /// <summary>Item de ComboBox com valor interno e texto amigável.</summary>
    internal sealed class Opcao
    {
        public string Valor { get; }
        public string Texto { get; }
        public Opcao(string valor, string texto) { Valor = valor; Texto = texto; }
        public override string ToString() => Texto;
    }

    public partial class MainWindow : Window
    {
        private bool _carregando = true;
        private bool _saindo;
        private string _pagina = "inicio";

        // editor de modo
        private ModoModel _editando;
        private bool _novoModo;
        private string _corEditando = "#3DDC84";
        private string _caminhoLauncher = "";
        private string _telaOriginal = "manter";
        private List<TelaInfo> _telas = new();
        private LayoutTelas _layoutEditando;

        // celular
        private string _codigoMostrado = "";
        private int _geracaoFirewall;

        // aparência
        private string _wpCarregado = "";

        // atualização
        private string _urlUpdate;

        private readonly DispatcherTimer _volTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
        private readonly DispatcherTimer _wpTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        private readonly DispatcherTimer _perfTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        private readonly DispatcherTimer _qrTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };

        private static readonly string[] CoresModo = { "#3DDC84", "#F56A0E", "#5B9BFF", "#A78BFA", "#F472B6", "#FACC15", "#22D3EE", "#F87171", "#94A3B8", "#10B981" };
        private static readonly string[] CoresAccent = { "#3DDC84", "#5B9BFF", "#F56A0E", "#A78BFA", "#F472B6", "#FACC15", "#22D3EE", "#F87171" };

        public MainWindow()
        {
            InitializeComponent();
            TxtVersaoLateral.Text = $"v{AppInfo.Versao} · {AppInfo.Edicao}";

            Closing += AoFechar;
            SourceInitialized += (s, e) => ThemeService.AplicarBarraTitulo(this);
            IsVisibleChanged += (s, e) => { if (IsVisible) AoMostrar(); else { _perfTimer.Stop(); _qrTimer.Stop(); } };
            PreviewKeyDown += AoTeclar;

            _volTimer.Tick += (s, e) => { _volTimer.Stop(); var v = (int)SldVolume.Value; Task.Run(() => AudioService.DefinirVolume(v)); AtualizarIconeMudo(false); };
            _wpTimer.Tick += (s, e) => { _wpTimer.Stop(); AplicarWallpaper(); };
            _perfTimer.Tick += (s, e) => AtualizarPerf();
            _qrTimer.Tick += (s, e) => AtualizarValidadeCodigo();

            EdTelaLayout.Checked += (s, e) => BlocoTelas.Visibility = Visibility.Visible;
            EdTelaLayout.Unchecked += (s, e) => BlocoTelas.Visibility = Visibility.Collapsed;

            EdLauncher.ItemsSource = LauncherService.Opcoes;
            EdEnergia.ItemsSource = new[] { "Não mexer", "Alto desempenho", "Equilibrado" };
            EdAtalho.TextChanged += (s, e) => ValidarAtalhoEditor();

            ModeService.ModoAtivoMudou += _ => Dispatcher.BeginInvoke(new Action(() => { if (_pagina == "inicio") AtualizarInicio(); }));
            ModeService.Trabalhando += t => Dispatcher.BeginInvoke(new Action(() => MostrarTrabalhando(t)));
            ConfigService.PerfilMudou += () => Dispatcher.BeginInvoke(new Action(AoMudarPerfil));
            App.CelularPareou += () => Dispatcher.BeginInvoke(new Action(() => { if (_pagina == "celular") AtualizarCelular(); else AtualizarGuia(); }));

            if (App.Controle != null)
            {
                App.Controle.ConexaoMudou += c => Dispatcher.BeginInvoke(new Action(AtualizarTextoControle));
                App.Controle.BateriaMudou += n => Dispatcher.BeginInvoke(new Action(AtualizarTextoControle));
                App.Controle.BotaoPressionado += b => GamepadNav.Tratar(this, b, () =>
                {
                    if (_pagina == "modo") VoltarParaInicio();
                    else if (_pagina != "inicio") { NavInicio.IsChecked = true; }
                });
            }
            // CORREÇÃO 4.1: se o controle já estava conectado antes da janela abrir, ela mostrava "Nenhum controle"
            AtualizarTextoControle();

            CarregarPerfis();
            CarregarAparencia();
            _carregando = false;
        }

        // ================= JANELA =================
        public void PermitirFechar() => _saindo = true;

        private void AoFechar(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_saindo) return;
            e.Cancel = true;                 // fecha para a bandeja
            Hide();
            ConfigService.SalvarAgora();
            if (!ConfigService.Atual.AvisoBandejaMostrado)
            {
                ConfigService.Atual.AvisoBandejaMostrado = true;
                ConfigService.Salvar();
                ToastWindow.Mostrar("O GameModeHub continua ligado", "Ele fica perto do relógio (bandeja). Para fechar de vez: Geral → Fechar o GameModeHub.", "ℹ");
            }
        }

        private void AoMostrar()
        {
            AplicarWallpaper();
            AtualizarSaudacao();
            MostrarPagina(_pagina == "modo" ? "modo" : _pagina);
        }

        private void AoTeclar(object sender, KeyEventArgs e)
        {
            bool caixaAtalho = Keyboard.FocusedElement is TextBox tb && HotkeyBox.GetAtivo(tb);
            if (e.Key == Key.Escape && _pagina == "modo" && !caixaAtalho) { VoltarParaInicio(); e.Handled = true; }
            else if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control && _pagina == "modo") { BtnSalvarModo_Click(null, null); e.Handled = true; }
            else if (e.Key == Key.F5 && !caixaAtalho) { MostrarPagina(_pagina); e.Handled = true; }
        }

        private void AtualizarSaudacao()
        {
            int h = DateTime.Now.Hour;
            TxtSaudacao.Text = h < 5 ? "Boa madrugada 🌙" : h < 12 ? "Bom dia ☀" : h < 18 ? "Boa tarde 👋" : "Boa noite 🌙";
        }

        private void AtualizarTextoControle()
        {
            var ctl = App.Controle;
            string t = ctl == null || !ctl.Conectado ? "🎮 Nenhum controle"
                     : ctl.Bateria >= 0 ? $"🎮 Controle · bateria {ctl.Bateria}%" : "🎮 Controle conectado";
            TxtControle.Text = t;
            TxtControleDet.Text = ctl == null || !ctl.Conectado ? "Nenhum controle conectado agora"
                     : ctl.Bateria >= 0 ? $"✔ Controle conectado · bateria {ctl.Bateria}%" : "✔ Controle conectado (com fio ou bateria desconhecida)";
        }

        // ================= NAVEGAÇÃO =================
        private void Nav_Checked(object sender, RoutedEventArgs e)
        {
            if (PgInicio == null) return;
            MostrarPagina((string)((RadioButton)sender).Tag);
        }

        private void MostrarPagina(string nome)
        {
            if (PgInicio == null) return;
            _pagina = nome;
            PgInicio.Visibility = nome == "inicio" ? Visibility.Visible : Visibility.Collapsed;
            PgModo.Visibility = nome == "modo" ? Visibility.Visible : Visibility.Collapsed;
            PgCelular.Visibility = nome == "celular" ? Visibility.Visible : Visibility.Collapsed;
            PgProgramas.Visibility = nome == "programas" ? Visibility.Visible : Visibility.Collapsed;
            PgControles.Visibility = nome == "controles" ? Visibility.Visible : Visibility.Collapsed;
            PgAparencia.Visibility = nome == "aparencia" ? Visibility.Visible : Visibility.Collapsed;
            PgGeral.Visibility = nome == "geral" ? Visibility.Visible : Visibility.Collapsed;

            _perfTimer.Stop(); _qrTimer.Stop();
            switch (nome)
            {
                case "inicio": AtualizarInicio(); _perfTimer.Start(); AtualizarPerf(); break;
                case "celular": AtualizarCelular(); _qrTimer.Start(); break;
                case "programas": AtualizarProgramas(); break;
                case "controles": CarregarControles(); break;
                case "aparencia": CarregarAparencia(); break;
                case "geral": CarregarGeral(); _perfTimer.Start(); AtualizarPerf(); break;
            }
        }

        private void IrPara(string nome)
        {
            var rb = nome switch { "celular" => NavCelular, "programas" => NavProgramas, "controles" => NavControles, "aparencia" => NavAparencia, "geral" => NavGeral, _ => NavInicio };
            if (rb.IsChecked == true) MostrarPagina(nome); else rb.IsChecked = true;
        }

        // ================= PERFIS =================
        private void CarregarPerfis()
        {
            bool antes = _carregando; _carregando = true;
            var c = ConfigService.Atual;
            var itens = c.Perfis.Select(kv => new Opcao(kv.Key, kv.Value.Nome)).ToList();
            CmbPerfil.ItemsSource = itens;
            CmbPerfil.SelectedItem = itens.FirstOrDefault(i => i.Valor == c.PerfilAtivo);
            _carregando = antes;
        }

        private void CmbPerfil_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_carregando || CmbPerfil.SelectedItem is not Opcao o) return;
            ConfigService.TrocarPerfil(o.Valor);
        }

        private void AoMudarPerfil()
        {
            CarregarPerfis();
            ToastWindow.Mostrar("Perfil: " + ConfigService.Perfil.Nome, $"{ConfigService.Perfil.Modos.Count} modo(s)", "👤");
            MostrarPagina(_pagina == "modo" ? "inicio" : _pagina);
            if (_pagina == "inicio") NavInicio.IsChecked = true;
        }

        // ================= INÍCIO =================
        private void AtualizarInicio()
        {
            bool antes = _carregando; _carregando = true;
            var perfil = ConfigService.Perfil;
            var ativo = perfil.Modos.FirstOrDefault(m => m.Id == ModeService.ModoAtivoId);
            TxtEstado.Text = ativo != null ? ativo.Nome : "Desktop normal";
            Brush corEstado = ativo != null ? new SolidColorBrush(ThemeService.CorDe(ativo.Cor, Colors.SeaGreen)) : (Brush)FindResource("MutedBrush");
            PontoEstado.Fill = corEstado; HaloEstado.Fill = corEstado;
            BtnRestaurar.Style = (Style)FindResource(ativo != null ? "BtnPrimario" : "BtnSecundario");

            AtualizarAudioInicio();

            PainelModos.Children.Clear();
            foreach (var m in perfil.Modos) PainelModos.Children.Add(CriarCartaoModo(m, m.Id == ModeService.ModoAtivoId));
            var novo = new Button { Style = (Style)FindResource("CartaoNovo"), Width = 272, Height = 136, Margin = new Thickness(0, 0, 14, 14), ToolTip = "Criar um modo novo" };
            novo.Content = new StackPanel { Children = { new TextBlock { Text = "＋", FontSize = 26, HorizontalAlignment = HorizontalAlignment.Center }, new TextBlock { Text = "Novo modo", FontSize = 14, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center } } };
            novo.Click += BtnNovoModo_Click;
            PainelModos.Children.Add(novo);

            AtualizarGuia();
            _carregando = antes;
        }

        private void AtualizarAudioInicio()
        {
            bool antes = _carregando; _carregando = true;
            var c = ConfigService.Atual;
            AudioA.Content = "🎧  A · " + Curto(c.AudioSaidaANome);
            AudioB.Content = "📺  B · " + Curto(c.AudioSaidaBNome);
            AudioA.IsChecked = c.AudioSlotAtivo != "B"; AudioB.IsChecked = c.AudioSlotAtivo == "B";
            int vol = AudioService.LerVolume();
            if (vol >= 0 && !_volTimer.IsEnabled && !SldVolume.IsMouseCaptureWithin) { SldVolume.Value = vol; TxtVolume.Text = vol + "%"; }
            AtualizarIconeMudo(AudioService.LerMudo());
            _carregando = antes;
        }

        private void AtualizarIconeMudo(bool mudo)
        {
            BtnMudo.Content = mudo ? "🔇" : SldVolume.Value < 1 ? "🔈" : SldVolume.Value < 50 ? "🔉" : "🔊";
            TxtVolume.Opacity = mudo ? 0.5 : 1;
        }

        private static string Curto(string s) => string.IsNullOrWhiteSpace(s) ? "não definida" : (s.Length > 24 ? s.Substring(0, 23) + "…" : s);

        private Border Chip(string texto, Brush frente = null, Brush fundo = null)
        {
            var b = new Border { Style = (Style)FindResource("Chip"), Margin = new Thickness(0, 0, 6, 0) };
            if (fundo != null) b.Background = fundo;
            var t = new TextBlock { Text = texto, FontSize = 11, FontWeight = FontWeights.SemiBold };
            if (frente != null) t.Foreground = frente; else t.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            b.Child = t;
            return b;
        }

        private UIElement CriarCartaoModo(ModoModel m, bool ativo)
        {
            var cor = ThemeService.CorDe(m.Cor, Color.FromRgb(0x3D, 0xDC, 0x84));
            var corBrush = new SolidColorBrush(cor);

            var pilha = new StackPanel();
            var topo = new DockPanel();
            var icone = new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(11), Background = new SolidColorBrush(Color.FromArgb(0x38, cor.R, cor.G, cor.B)), Margin = new Thickness(0, 0, 12, 0) };
            icone.Child = new System.Windows.Shapes.Ellipse { Width = 13, Height = 13, Fill = corBrush };
            DockPanel.SetDock(icone, Dock.Left);
            topo.Children.Add(icone);
            topo.Children.Add(new TextBlock { Text = m.Nome, FontSize = 16, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 36, 0) });
            pilha.Children.Add(topo);
            pilha.Children.Add(new TextBlock { Text = ResumoModo(m), Style = (Style)FindResource("Sub"), Margin = new Thickness(0, 10, 0, 0), MaxHeight = 34, TextTrimming = TextTrimming.CharacterEllipsis });

            var rodape = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            if (ativo)
            {
                double lum = (0.299 * cor.R + 0.587 * cor.G + 0.114 * cor.B) / 255.0;
                rodape.Children.Add(Chip("● ATIVO", new SolidColorBrush(lum > 0.55 ? Color.FromRgb(6, 20, 12) : Colors.White), corBrush));
            }
            if (!string.IsNullOrWhiteSpace(m.TeclaAtalho)) rodape.Children.Add(Chip("⌨ " + m.TeclaAtalho));
            var c = ConfigService.Atual;
            var padrao = string.IsNullOrEmpty(ConfigService.Perfil.ModoIdAtalhoPadrao) ? ConfigService.Perfil.Modos.FirstOrDefault()?.Id : ConfigService.Perfil.ModoIdAtalhoPadrao;
            if (c.CombosControle && padrao == m.Id) rodape.Children.Add(Chip("🎮 LB+RB+Start"));
            if (rodape.Children.Count > 0) pilha.Children.Add(rodape);

            var botao = new Button
            {
                Style = (Style)FindResource("CartaoModo"), Content = pilha, Width = 272, MinHeight = 136,
                HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Top,
                ToolTip = ativo ? "Modo ativo agora — clique para aplicar de novo" : "Ativar “" + m.Nome + "”"
            };
            if (ativo) { botao.BorderBrush = corBrush; botao.BorderThickness = new Thickness(2); }
            var id = m.Id;
            botao.Click += (s, e) => AtivarModo(id);
            botao.ContextMenu = MenuDoModo(id);

            var editar = new Button { Style = (Style)FindResource("BtnIcone"), Content = "✎", HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 12, 12, 0), ToolTip = "Editar modo" };
            editar.Click += (s, e) => { var mm = ConfigService.Perfil.Modos.FirstOrDefault(x => x.Id == id); if (mm != null) AbrirEditor(mm, false); };

            var g = new Grid { Margin = new Thickness(0, 0, 14, 14) };
            g.Children.Add(botao); g.Children.Add(editar);
            return g;
        }

        private ContextMenu MenuDoModo(string id)
        {
            var menu = new ContextMenu();
            MenuItem Item(string texto, Action acao) { var mi = new MenuItem { Header = texto }; mi.Click += (s, e) => acao(); menu.Items.Add(mi); return mi; }
            ModoModel Achar() => ConfigService.Perfil.Modos.FirstOrDefault(x => x.Id == id);
            Item("▶   Ativar", () => AtivarModo(id));
            Item("✎   Editar", () => { var m = Achar(); if (m != null) AbrirEditor(m, false); });
            Item("⧉   Duplicar", () => { var m = Achar(); if (m != null) DuplicarModo(m, false); });
            Item("🖱   Criar atalho na área de trabalho", () => { var m = Achar(); if (m != null) CriarAtalhoModo(m); });
            Item("🎮   Usar no LB + RB + Start", () => { ConfigService.Perfil.ModoIdAtalhoPadrao = id; ConfigService.Salvar(true); AtualizarInicio(); ToastWindow.Mostrar("Combo do controle", "LB + RB + Start agora ativa este modo.", "🎮"); });
            menu.Items.Add(new Separator());
            Item("🗑   Excluir", () => { var m = Achar(); if (m != null) ExcluirModo(m); });
            return menu;
        }

        private static string ResumoModo(ModoModel m)
        {
            string tela = m.Tela switch
            {
                "manter" => "Telas como estão",
                "extend" => "Estende as telas",
                "clone" => "Duplica as telas",
                "internal" => "Só tela principal",
                "external" => "Só tela externa",
                _ when m.Tela != null && m.Tela.StartsWith("layout:") =>
                    ConfigService.Layouts.FirstOrDefault(l => l.Id == m.Tela.Substring(7)) is LayoutTelas l && l.NomesAtivos.Count > 0
                        ? "Acesas: " + string.Join(" + ", l.NomesAtivos) : "⚠ Telas ainda não escolhidas",
                _ => m.Tela
            };
            string aud = m.UsarSaida == "A" ? " · Áudio A" : m.UsarSaida == "B" ? " · Áudio B" : "";
            return tela + aud + (m.Launcher != "Nenhum" ? " · " + LauncherService.Descricao(m) : "");
        }

        private void MostrarTrabalhando(string texto)
        {
            bool ocupado = texto != null;
            BannerTrabalhando.Visibility = ocupado ? Visibility.Visible : Visibility.Collapsed;
            TxtTrabalhando.Text = texto ?? "";
            PainelModos.IsEnabled = !ocupado;
            BtnRestaurar.IsEnabled = !ocupado;
            BtnAcender.IsEnabled = !ocupado;
            BtnTestarModo.IsEnabled = !ocupado;
        }

        private async void AtivarModo(string id)
        {
            var r = await Task.Run(() => ModeService.Ativar(id));
            if (!r.Ok) ToastWindow.Mostrar("Algo não saiu como esperado", r.Resumo, "⚠");
            if (_pagina == "inicio") AtualizarInicio();
        }

        private async void BtnRestaurar_Click(object sender, RoutedEventArgs e)
        {
            var r = await Task.Run(() => ModeService.Restaurar());
            if (!r.Ok) ToastWindow.Mostrar("Restaurar", r.Resumo, "⚠");
            if (_pagina == "inicio") AtualizarInicio();
        }

        private async void BtnAcender_Click(object sender, RoutedEventArgs e)
        {
            BtnAcender.IsEnabled = false;
            var r = await Task.Run(() => DisplayService.AcenderTodas());
            BtnAcender.IsEnabled = true;
            ToastWindow.Mostrar(r.ok ? "Todas as telas acesas" : "Não consegui", r.msg, r.ok ? "💡" : "⚠");
        }

        private void BtnNovoModo_Click(object sender, RoutedEventArgs e) =>
            AbrirEditor(new ModoModel { Id = ConfigService.NovoId(), Nome = "", Tela = "manter", Launcher = "Nenhum", UsarSaida = "Nenhum", Cor = CoresModo[ConfigService.Perfil.Modos.Count % CoresModo.Length] }, true);

        private void BtnModoTv_Click(object sender, RoutedEventArgs e) => App.AbrirTv();

        private void Audio_Click(object sender, RoutedEventArgs e)
        {
            if (_carregando) return;
            var slot = (string)((RadioButton)sender).Tag;
            Task.Run(() => AudioService.AplicarSlot(slot)).ContinueWith(t => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (t.Result == null) ToastWindow.Mostrar("Saída " + slot + " não encontrada", "Escolha os aparelhos em Geral → Saídas de áudio.", "⚠");
                AtualizarAudioInicio();
            })));
        }

        private void BtnMudo_Click(object sender, RoutedEventArgs e)
        {
            var m = AudioService.DefinirMudo();
            if (m.HasValue) AtualizarIconeMudo(m.Value);
        }

        private void BtnIrSaidas_Click(object sender, RoutedEventArgs e) => IrPara("geral");

        private void SldVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtVolume == null) return;
            TxtVolume.Text = (int)e.NewValue + "%";
            if (_carregando) return;
            _volTimer.Stop(); _volTimer.Start();
        }

        // ---- Primeiros passos ----
        private void AtualizarGuia()
        {
            var c = ConfigService.Atual;
            var perfil = ConfigService.Perfil;
            bool audioOk = !string.IsNullOrEmpty(c.AudioSaidaACmd) && !string.IsNullOrEmpty(c.AudioSaidaBCmd);
            var semTela = perfil.Modos.FirstOrDefault(m => m.Tela != null && m.Tela.StartsWith("layout:") &&
                            !(ConfigService.Layouts.FirstOrDefault(l => l.Id == m.Tela.Substring(7)) is LayoutTelas l && l.TelasAtivas.Count > 0));
            bool celularOk = c.Pareados.Count > 0;

            if (c.GuiaInicialOculto || (audioOk && semTela == null && celularOk)) { CardGuia.Visibility = Visibility.Collapsed; return; }
            CardGuia.Visibility = Visibility.Visible;
            ListaGuia.Children.Clear();
            void Passo(bool feito, string texto, string botao, Action acao)
            {
                var d = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
                if (!feito)
                {
                    var b = new Button { Style = (Style)FindResource("BtnSecundario"), Content = botao, Padding = new Thickness(12, 6, 12, 6) };
                    b.Click += (s, e) => acao();
                    DockPanel.SetDock(b, Dock.Right); d.Children.Add(b);
                }
                var t = new TextBlock { Text = (feito ? "✔   " : "○   ") + texto, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
                if (feito) { t.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); t.TextDecorations = TextDecorations.Strikethrough; }
                d.Children.Add(t);
                ListaGuia.Children.Add(d);
            }
            Passo(audioOk, "Escolha qual aparelho é a saída de áudio A (fones) e qual é a B (TV/caixas)", "Escolher", () => IrPara("geral"));
            Passo(semTela == null, semTela != null ? $"Marque quais telas ficam acesas no modo “{semTela.Nome}”" : "Telas de cada modo escolhidas", "Escolher telas", () => { if (semTela != null) AbrirEditor(semTela, false); });
            Passo(celularOk, "Pareie o celular para usar como controle remoto (opcional)", "Parear", () => IrPara("celular"));
        }

        private void BtnOcultarGuia_Click(object sender, RoutedEventArgs e)
        {
            ConfigService.Atual.GuiaInicialOculto = true; ConfigService.Salvar(true);
            CardGuia.Visibility = Visibility.Collapsed;
        }

        // ================= EDITOR DE MODO =================
        private void AbrirEditor(ModoModel m, bool novo)
        {
            _carregando = true;
            _editando = m; _novoModo = novo; _telaOriginal = m.Tela ?? "manter";
            _corEditando = string.IsNullOrWhiteSpace(m.Cor) ? "#3DDC84" : m.Cor; _caminhoLauncher = m.LauncherCaminho ?? "";
            TxtTituloEditor.Text = novo ? "Novo modo" : "Editar “" + m.Nome + "”";
            BtnExcluirModo.Visibility = novo ? Visibility.Collapsed : Visibility.Visible;
            BtnDuplicarModo.Visibility = novo ? Visibility.Collapsed : Visibility.Visible;

            EdNome.Text = m.Nome;
            EdTelaManter.IsChecked = EdTelaLayout.IsChecked = EdTelaExtend.IsChecked = EdTelaClone.IsChecked = false;
            if (m.Tela != null && m.Tela.StartsWith("layout:")) EdTelaLayout.IsChecked = true;
            else if (m.Tela == "extend") EdTelaExtend.IsChecked = true;
            else if (m.Tela == "clone") EdTelaClone.IsChecked = true;
            else EdTelaManter.IsChecked = true;
            BlocoTelas.Visibility = EdTelaLayout.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

            _layoutEditando = m.Tela != null && m.Tela.StartsWith("layout:") ? ConfigService.Layouts.FirstOrDefault(l => l.Id == m.Tela.Substring(7)) : null;
            CarregarListaTelas();

            var c = ConfigService.Atual;
            EdAudioNenhum.IsChecked = m.UsarSaida != "A" && m.UsarSaida != "B";
            EdAudioA.IsChecked = m.UsarSaida == "A"; EdAudioB.IsChecked = m.UsarSaida == "B";
            EdAudioA.Content = "Saída A · " + Curto(c.AudioSaidaANome);
            EdAudioB.Content = "Saída B · " + Curto(c.AudioSaidaBNome);
            TxtAudioEditor.Text = string.IsNullOrEmpty(c.AudioSaidaACmd) || string.IsNullOrEmpty(c.AudioSaidaBCmd)
                ? "Dica: escolha os aparelhos de A e B em Geral → Saídas de áudio." : "";

            EdLauncher.SelectedItem = LauncherService.Opcoes.Contains(m.Launcher) ? m.Launcher : (string.IsNullOrEmpty(m.LauncherCaminho) ? "Nenhum" : "Outro...");
            AtualizarDetalheLauncher();
            EdHdr.IsChecked = m.Hdr; EdGentil.IsChecked = m.FecharGentil;
            EdLiberarRam.IsChecked = m.LiberarRam; EdEsconderCursor.IsChecked = m.EsconderCursor;
            EdEnergia.SelectedIndex = m.PlanoEnergia == "alto" ? 1 : m.PlanoEnergia == "equilibrado" ? 2 : 0;
            EdAtalho.Text = m.TeclaAtalho ?? "";
            ValidarAtalhoEditor();
            MontarCores();

            NavInicio.IsChecked = NavCelular.IsChecked = NavProgramas.IsChecked = NavControles.IsChecked = NavAparencia.IsChecked = NavGeral.IsChecked = false;
            MostrarPagina("modo");
            PgModo.ScrollToTop();
            _carregando = false;
            if (novo) EdNome.Focus();
        }

        private void CarregarListaTelas()
        {
            PainelTelas.Children.Clear();
            _telas = DisplayService.Listar();
            var marcadas = _layoutEditando?.TelasAtivas ?? new List<string>();
            foreach (var t in _telas)
            {
                var info = (t.Ativa ? "acesa agora" : "apagada agora") + (t.Principal ? " · principal" : "") + (t.HdrSuportado ? " · HDR" : "");
                var cb = new CheckBox
                {
                    Style = (Style)FindResource("Interruptor"), Tag = new[] { t.Path, t.Nome }, Margin = new Thickness(0, 0, 0, 10),
                    Content = new TextBlock { Inlines = { new System.Windows.Documents.Run(t.Nome) { FontWeight = FontWeights.SemiBold }, new System.Windows.Documents.Run("   " + info) { Foreground = (Brush)FindResource("MutedBrush"), FontSize = 12 } } },
                    IsChecked = marcadas.Contains(t.Path, StringComparer.OrdinalIgnoreCase)
                };
                PainelTelas.Children.Add(cb);
            }
            // telas salvas no layout que não estão conectadas agora: mantém visível para não perder a escolha
            if (_layoutEditando != null)
                for (int i = 0; i < _layoutEditando.TelasAtivas.Count; i++)
                {
                    var p = _layoutEditando.TelasAtivas[i];
                    if (_telas.Any(t => string.Equals(t.Path, p, StringComparison.OrdinalIgnoreCase))) continue;
                    var nome = i < _layoutEditando.NomesAtivos.Count ? _layoutEditando.NomesAtivos[i] : "Tela";
                    PainelTelas.Children.Add(new CheckBox
                    {
                        Style = (Style)FindResource("Interruptor"), Tag = new[] { p, nome }, Margin = new Thickness(0, 0, 0, 10), IsChecked = true,
                        Content = new TextBlock { Inlines = { new System.Windows.Documents.Run(nome) { FontWeight = FontWeights.SemiBold }, new System.Windows.Documents.Run("   desconectada agora (TV desligada?)") { Foreground = (Brush)FindResource("MutedBrush"), FontSize = 12 } } }
                    });
                }
            if (PainelTelas.Children.Count == 0)
                PainelTelas.Children.Add(new TextBlock { Text = "Nenhuma tela encontrada.", Style = (Style)FindResource("Sub") });
        }

        private List<CheckBox> TelasMarcadas() => PainelTelas.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).ToList();

        private void BtnAtualizarTelas_Click(object sender, RoutedEventArgs e)
        {
            // guarda o que estava marcado antes de recarregar
            // CORREÇÃO 4.1: o nome da tela era cortado na primeira palavra ("LG ULTRAGEAR" virava "LG")
            var marc = TelasMarcadas();
            _layoutEditando = new LayoutTelas
            {
                Id = _editando.Id,
                TelasAtivas = marc.Select(c => ((string[])c.Tag)[0]).ToList(),
                NomesAtivos = marc.Select(c => ((string[])c.Tag)[1]).ToList()
            };
            CarregarListaTelas();
        }

        private void MontarCores()
        {
            PainelCores.Children.Clear();
            var lista = CoresModo.ToList();
            if (!lista.Any(h => h.Equals(_corEditando, StringComparison.OrdinalIgnoreCase))) lista.Add(_corEditando);
            foreach (var hex in lista)
            {
                var b = new Button { Style = (Style)FindResource("BolinhaCor"), Tag = hex, Background = new SolidColorBrush(ThemeService.CorDe(hex, Colors.Gray)), ToolTip = hex.ToUpperInvariant() };
                if (hex.Equals(_corEditando, StringComparison.OrdinalIgnoreCase)) b.SetResourceReference(BorderBrushProperty, "SelecaoBrush");
                b.Click += (s, e) => { _corEditando = (string)((Button)s).Tag; MontarCores(); };
                PainelCores.Children.Add(b);
            }
            var mais = new Button { Style = (Style)FindResource("BolinhaCor"), Content = "＋", ToolTip = "Escolher qualquer cor…" };
            mais.SetResourceReference(BackgroundProperty, "Surface2Brush");
            mais.SetResourceReference(ForegroundProperty, "TextBrush");
            mais.Click += (s, e) => { var h = EscolherCor(_corEditando); if (h != null) { _corEditando = h; MontarCores(); } };
            PainelCores.Children.Add(mais);
        }

        /// <summary>Paleta completa do Windows (como o criador de cor RGB da v3).</summary>
        private string EscolherCor(string atual)
        {
            var c = ThemeService.CorDe(atual, Colors.SeaGreen);
            using var dlg = new System.Windows.Forms.ColorDialog { FullOpen = true, AnyColor = true, Color = System.Drawing.Color.FromArgb(c.R, c.G, c.B) };
            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return null;
            return $"#{dlg.Color.R:X2}{dlg.Color.G:X2}{dlg.Color.B:X2}";
        }

        private void EdLauncher_SelectionChanged(object sender, SelectionChangedEventArgs e) => AtualizarDetalheLauncher();

        private void AtualizarDetalheLauncher()
        {
            if (TxtLauncherDet == null) return;
            var l = EdLauncher.SelectedItem as string ?? "Nenhum";
            BtnEscolherLauncher.Visibility = l == "Nenhum" ? Visibility.Collapsed : Visibility.Visible;
            if (l == "Nenhum") TxtLauncherDet.Text = "Nada será aberto.";
            else if (l == "Outro...") TxtLauncherDet.Text = string.IsNullOrEmpty(_caminhoLauncher) ? "⚠ Clique em “Escolher programa…”." : "Programa: " + _caminhoLauncher;
            else
            {
                var d = LauncherService.Detectado(l);
                TxtLauncherDet.Text = d != null ? "✔ Detectado: " + d : "⚠ Não encontrei " + l + " neste PC. Use “Escolher programa…”.";
            }
        }

        private void BtnEscolherLauncher_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Programas (*.exe)|*.exe", Title = "Escolha o programa a abrir" };
            if (dlg.ShowDialog(this) == true) { _caminhoLauncher = dlg.FileName; EdLauncher.SelectedItem = "Outro..."; AtualizarDetalheLauncher(); }
        }

        private void BtnLimparAtalho_Click(object sender, RoutedEventArgs e) => EdAtalho.Text = "";

        /// <summary>Mostra na hora se o atalho escolhido já é usado por outro modo/ação.</summary>
        private string ConflitoAtalho(string atalho, string ignorarModoId)
        {
            if (string.IsNullOrWhiteSpace(atalho)) return null;
            var c = ConfigService.Atual;
            var outro = ConfigService.Perfil.Modos.FirstOrDefault(m => m.Id != ignorarModoId && HotkeyService.Iguais(m.TeclaAtalho, atalho));
            if (outro != null) return $"já é usado pelo modo “{outro.Nome}”";
            if (HotkeyService.Iguais(c.TeclaRestaurar, atalho)) return "já é usado por “Restaurar desktop”";
            if (HotkeyService.Iguais(c.TeclaFecharJogo, atalho)) return "já é usado por “Fechar jogo travado”";
            if (HotkeyService.Iguais(c.TeclaModoTv, atalho)) return "já é usado por “Abrir Modo TV”";
            return null;
        }

        private void ValidarAtalhoEditor()
        {
            if (_editando == null) return;
            var conflito = ConflitoAtalho(EdAtalho.Text.Trim(), _editando.Id);
            TxtAvisoAtalho.Text = conflito != null ? "⚠ Esse atalho " + conflito : "";
            TxtAvisoAtalho.Visibility = conflito != null ? Visibility.Visible : Visibility.Collapsed;
        }

        private bool SalvarEdicao()
        {
            var m = _editando;
            if (m == null) return false;
            if (string.IsNullOrWhiteSpace(EdNome.Text)) { ToastWindow.Mostrar("Dê um nome ao modo", "Ex.: Jogar na TV", "⚠"); EdNome.Focus(); return false; }

            var atalho = EdAtalho.Text.Trim();
            if (atalho.Length > 0 && !HotkeyService.Analisar(atalho, out _, out _)) { ToastWindow.Mostrar("Atalho inválido", "Clique na caixa e aperte a tecla (ex.: NumPad1, F6, Ctrl+Alt+G).", "⚠"); return false; }
            var conflito = ConflitoAtalho(atalho, m.Id);
            if (conflito != null) { ToastWindow.Mostrar("Atalho repetido", "Esse atalho " + conflito + ".", "⚠"); return false; }

            var launcher = EdLauncher.SelectedItem as string ?? "Nenhum";
            if (launcher == "Outro..." && string.IsNullOrWhiteSpace(_caminhoLauncher)) { ToastWindow.Mostrar("Escolha o programa", "Clique em “Escolher programa…” ou selecione outra opção.", "⚠"); return false; }

            if (EdTelaLayout.IsChecked == true)
            {
                var marc = TelasMarcadas();
                if (marc.Count == 0) { ToastWindow.Mostrar("Marque pelo menos uma tela", "Senão o modo deixaria todas apagadas.", "⚠"); return false; }
                var lay = ConfigService.Layouts.FirstOrDefault(l => l.Id == m.Id);
                if (lay == null) { lay = new LayoutTelas { Id = m.Id }; ConfigService.Layouts.Add(lay); }
                lay.Nome = EdNome.Text.Trim();
                lay.TelasAtivas = marc.Select(c => ((string[])c.Tag)[0]).ToList();
                lay.NomesAtivos = marc.Select(c => ((string[])c.Tag)[1]).ToList();
                lay.PrincipalPath = lay.TelasAtivas[0];
                m.Tela = "layout:" + m.Id;
            }
            else if (EdTelaExtend.IsChecked == true) m.Tela = "extend";
            else if (EdTelaClone.IsChecked == true) m.Tela = "clone";
            else m.Tela = (_telaOriginal == "internal" || _telaOriginal == "external") ? _telaOriginal : "manter";

            m.Nome = EdNome.Text.Trim();
            m.UsarSaida = EdAudioA.IsChecked == true ? "A" : EdAudioB.IsChecked == true ? "B" : "Nenhum";
            m.Launcher = launcher;
            m.LauncherCaminho = launcher == "Outro..." ? _caminhoLauncher : "";
            m.Hdr = EdHdr.IsChecked == true; m.FecharGentil = EdGentil.IsChecked == true;
            m.LiberarRam = EdLiberarRam.IsChecked == true; m.EsconderCursor = EdEsconderCursor.IsChecked == true;
            m.PlanoEnergia = EdEnergia.SelectedIndex == 1 ? "alto" : EdEnergia.SelectedIndex == 2 ? "equilibrado" : "";
            m.TeclaAtalho = atalho; m.Cor = _corEditando;

            if (_novoModo && !ConfigService.Perfil.Modos.Contains(m)) ConfigService.Perfil.Modos.Add(m);
            _novoModo = false;
            ConfigService.Salvar(true);
            var falhas = App.RegistrarAtalhos();
            if (atalho.Length > 0 && falhas.Any(f => f.StartsWith(atalho + " ", StringComparison.OrdinalIgnoreCase)))
                ToastWindow.Mostrar("Modo salvo, mas o atalho não funcionou", atalho + " já é usado por outro programa do PC. Escolha outra tecla.", "⚠");
            return true;
        }

        private void VoltarParaInicio() { NavInicio.IsChecked = true; MostrarPagina("inicio"); }
        private void BtnVoltar_Click(object sender, RoutedEventArgs e) => VoltarParaInicio();

        private void BtnSalvarModo_Click(object sender, RoutedEventArgs e)
        {
            if (!SalvarEdicao()) return;
            ToastWindow.Mostrar("Modo salvo", _editando.Nome, "✔");
            VoltarParaInicio();
        }

        private async void BtnTestarModo_Click(object sender, RoutedEventArgs e)
        {
            if (!SalvarEdicao()) return;
            var id = _editando.Id;
            VoltarParaInicio();
            var r = await Task.Run(() => ModeService.Ativar(id));
            if (!r.Ok) ToastWindow.Mostrar("Algo não saiu como esperado", r.Resumo, "⚠");
            if (_pagina == "inicio") AtualizarInicio();
        }

        private void BtnDuplicarModo_Click(object sender, RoutedEventArgs e)
        {
            if (!SalvarEdicao()) return;
            DuplicarModo(_editando, true);
        }

        private void DuplicarModo(ModoModel m, bool abrirEditor)
        {
            var copia = JsonSerializer.Deserialize<ModoModel>(JsonSerializer.Serialize(m));
            copia.Id = ConfigService.NovoId();
            copia.Nome = (m.Nome + " (cópia)").Length > 40 ? (m.Nome + " (cópia)").Substring(0, 40) : m.Nome + " (cópia)";
            copia.TeclaAtalho = "";                  // atalho não pode repetir
            if (m.Tela != null && m.Tela.StartsWith("layout:"))
            {
                var lay = ConfigService.Layouts.FirstOrDefault(l => l.Id == m.Tela.Substring(7));
                if (lay != null)
                {
                    ConfigService.Layouts.Add(new LayoutTelas { Id = copia.Id, Nome = copia.Nome, TelasAtivas = lay.TelasAtivas.ToList(), NomesAtivos = lay.NomesAtivos.ToList(), PrincipalPath = lay.PrincipalPath });
                    copia.Tela = "layout:" + copia.Id;
                }
                else copia.Tela = "manter";
            }
            var lista = ConfigService.Perfil.Modos;
            int i = lista.IndexOf(m);
            lista.Insert(i < 0 ? lista.Count : i + 1, copia);
            ConfigService.Salvar(true);
            ToastWindow.Mostrar("Modo duplicado", copia.Nome, "⧉");
            if (abrirEditor) AbrirEditor(copia, false); else AtualizarInicio();
        }

        private void BtnAtalhoDesktop_Click(object sender, RoutedEventArgs e)
        {
            if (!SalvarEdicao()) return;
            CriarAtalhoModo(_editando);
        }

        private void CriarAtalhoModo(ModoModel m)
        {
            var r = SystemService.CriarAtalhoAreaDeTrabalho("GameModeHub - " + m.Nome, "--modo " + m.Id, "Ativa o modo " + m.Nome + " no GameModeHub");
            if (r != null)
                ToastWindow.Mostrar("Atalho criado na área de trabalho", Paths.Portable ? "Se mover a pasta do GameModeHub, crie o atalho de novo." : "Dois cliques nele ativam o modo “" + m.Nome + "”.", "🖱");
            else ToastWindow.Mostrar("Não consegui criar o atalho", "Veja o log em Geral → Ver log.", "⚠");
        }

        private void BtnExcluirModo_Click(object sender, RoutedEventArgs e) => ExcluirModo(_editando);

        private void ExcluirModo(ModoModel m)
        {
            if (m == null) return;
            if (MessageBox.Show(this, $"Excluir o modo “{m.Nome}”?\n\nIsso não pode ser desfeito.", "GameModeHub", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            var perfil = ConfigService.Perfil;
            perfil.Modos.Remove(m);
            // só apaga o layout se nenhum outro modo (de qualquer perfil) usa
            if (!ConfigService.Atual.Perfis.Values.SelectMany(p => p.Modos).Any(x => x.Tela == "layout:" + m.Id))
                ConfigService.Layouts.RemoveAll(l => l.Id == m.Id);
            if (perfil.ModoIdAtalhoPadrao == m.Id) perfil.ModoIdAtalhoPadrao = "";
            if (ConfigService.Atual.RestaurarTelas == "modo:" + m.Id) ConfigService.Atual.RestaurarTelas = "auto";
            ConfigService.Salvar(true);
            App.RegistrarAtalhos();
            ToastWindow.Mostrar("Modo excluído", m.Nome, "🗑");
            VoltarParaInicio();
        }

        // ================= CELULAR =================
        private void AtualizarCelular()
        {
            var srv = App.Servidor;
            var c = ConfigService.Atual;
            RenderizarQr();
            TxtServidor.Text = srv.Rodando ? "● Servidor ativo" : "● Servidor parado: " + srv.Erro;
            TxtServidor.SetResourceReference(TextBlock.ForegroundProperty, srv.Rodando ? "AccentBrush" : "DangerBrush");

            ListaPareados.Children.Clear();
            if (c.Pareados.Count == 0) ListaPareados.Children.Add(new TextBlock { Text = "Nenhum aparelho pareado ainda.", Style = (Style)FindResource("Sub") });
            foreach (var p in c.Pareados.ToList())
            {
                var linha = new Border { Style = (Style)FindResource("Card"), Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(16, 12, 16, 12) };
                var dock = new DockPanel();
                var rem = new Button { Style = (Style)FindResource("BtnPerigo"), Content = "Remover", Padding = new Thickness(12, 6, 12, 6), ToolTip = "Esse aparelho deixa de controlar o PC na hora" };
                DockPanel.SetDock(rem, Dock.Right);
                var token = p.Token; var nome = p.Nome;
                rem.Click += (s, e) =>
                {
                    if (MessageBox.Show(this, $"Remover “{nome}”? Ele não vai mais conseguir controlar o PC (dá para parear de novo depois).", "GameModeHub", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                    ConfigService.Atual.Pareados.RemoveAll(x => x.Token == token); ConfigService.Salvar(true); AtualizarCelular();
                };
                dock.Children.Add(rem);
                var txt = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                txt.Children.Add(new TextBlock { Text = "📱  " + p.Nome, FontWeight = FontWeights.SemiBold });
                txt.Children.Add(new TextBlock { Text = "Pareado em " + p.Quando.ToString("dd/MM/yyyy 'às' HH:mm"), Style = (Style)FindResource("Sub") });
                dock.Children.Add(txt);
                linha.Child = dock; ListaPareados.Children.Add(linha);
            }

            TxtFirewall.Text = "Verificando firewall…";
            int geracao = ++_geracaoFirewall;
            int porta = c.Porta;
            Task.Run(() => SystemService.FirewallLiberado(porta)).ContinueWith(t =>
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (geracao != _geracaoFirewall) return;
                    TxtFirewall.Text = t.Result ? "✔ Firewall liberado para o GameModeHub (porta " + porta + ")" : "⚠ Firewall ainda não liberado — o celular pode não achar o PC";
                    BtnFirewall.Visibility = t.Result ? Visibility.Collapsed : Visibility.Visible;
                })));
        }

        private void RenderizarQr()
        {
            var srv = App.Servidor;
            var net = NetInfo.Principal();
            var c = ConfigService.Atual;
            var codigo = srv.CodigoPareamento;
            _codigoMostrado = codigo;
            TxtCodigo.Text = codigo.Insert(3, " ");
            TxtEndereco.Text = net != null ? $"Endereço do PC: {net.Ip}:{c.Porta}" : "⚠ Sem rede detectada — conecte o PC ao Wi-Fi/cabo";
            AtualizarValidadeCodigo();
            if (net == null) { ImgQr.Source = null; return; }
            var payload = JsonSerializer.Serialize(new { ip = net.Ip.ToString(), porta = c.Porta, mac = net.Mac, pc = Environment.MachineName, code = codigo });
            try
            {
                using var gen = new QRCodeGenerator();
                using var dados = gen.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
                var png = new PngByteQRCode(dados).GetGraphic(10);
                var bmp = new BitmapImage();
                bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.StreamSource = new MemoryStream(png); bmp.EndInit(); bmp.Freeze();
                ImgQr.Source = bmp;
            }
            catch (Exception ex) { Log.Error("QR", ex); }
        }

        /// <summary>
        /// CORREÇÃO 4.1: o código vence a cada 10 min e também muda depois de cada pareamento, mas o QR na tela
        /// continuava o antigo (o celular recebia "código incorreto"). Agora o QR se atualiza sozinho e mostra a validade.
        /// </summary>
        private void AtualizarValidadeCodigo()
        {
            var srv = App.Servidor;
            if (srv == null) return;
            if (srv.CodigoPareamento != _codigoMostrado) { RenderizarQr(); return; }
            var r = srv.CodigoRestante;
            TxtValidade.Text = $"Válido por mais {(int)r.TotalMinutes}:{r.Seconds:00} · depois troca sozinho";
        }

        private void BtnNovoCodigo_Click(object sender, RoutedEventArgs e) { App.Servidor.RegenerarCodigo(); RenderizarQr(); }

        private async void BtnFirewall_Click(object sender, RoutedEventArgs e)
        {
            BtnFirewall.IsEnabled = false;
            TxtFirewall.Text = "Aguardando a permissão do Windows…";
            var ok = await Task.Run(() => SystemService.LiberarFirewall(ConfigService.Atual.Porta));
            BtnFirewall.IsEnabled = true;
            ToastWindow.Mostrar(ok ? "Firewall liberado" : "Não foi liberado", ok ? "Agora o celular consegue achar o PC." : "Clique em “Sim” no aviso do Windows para continuar.", ok ? "🛡" : "⚠");
            AtualizarCelular();
        }

        // ================= PROGRAMAS =================
        private void AtualizarProgramas()
        {
            ListaApps.Children.Clear();
            var perfil = ConfigService.Perfil;
            TxtPerfilProgramas.Text = $"Lista do perfil “{perfil.Nome}”. Desligue o interruptor para manter um programa na lista sem fechá-lo.";
            if (perfil.AppsMonitorados.Count == 0)
                ListaApps.Children.Add(new TextBlock { Text = "Nenhum programa na lista ainda. Adicione abaixo os que devem fechar ao ativar um modo.", Style = (Style)FindResource("Sub") });
            foreach (var kv in perfil.AppsMonitorados.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase).ToList())
            {
                var nome = kv.Key;
                var dock = new DockPanel { Margin = new Thickness(0, 5, 0, 5) };
                var del = new Button { Style = (Style)FindResource("BtnIcone"), Content = "✕", ToolTip = "Tirar da lista" };
                DockPanel.SetDock(del, Dock.Right);
                del.Click += (s, e) => { perfil.AppsMonitorados.Remove(nome); ConfigService.Salvar(true); AtualizarProgramas(); };
                var chk = new CheckBox { Style = (Style)FindResource("Interruptor"), IsChecked = kv.Value, Content = nome, VerticalAlignment = VerticalAlignment.Center, ToolTip = kv.Value ? "Fecha ao ativar um modo" : "Não fecha (desligado)" };
                chk.Click += (s, e) => { perfil.AppsMonitorados[nome] = chk.IsChecked == true; ConfigService.Salvar(); };
                dock.Children.Add(del); dock.Children.Add(chk);
                ListaApps.Children.Add(dock);
            }
            AtualizarAbertos();
        }

        /// <summary>CORREÇÃO 4.1: a lista de programas abertos travava a janela por 1-2 s; agora carrega em segundo plano.</summary>
        private async void AtualizarAbertos()
        {
            var perfil = ConfigService.Perfil;
            BtnAtualizarAbertos.IsEnabled = false;
            CmbAbertos.ItemsSource = new[] { "Carregando…" };
            CmbAbertos.SelectedIndex = 0; CmbAbertos.IsEnabled = false;
            var nomes = await Task.Run(() => ProcessService.ListarAbertos().Select(a => a.Exe).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
            var lista = nomes.Where(n => !perfil.AppsMonitorados.ContainsKey(n)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            CmbAbertos.ItemsSource = lista;
            CmbAbertos.IsEnabled = lista.Count > 0;
            CmbAbertos.SelectedIndex = lista.Count > 0 ? 0 : -1;
            BtnAtualizarAbertos.IsEnabled = true;
        }

        private void BtnAtualizarAbertos_Click(object sender, RoutedEventArgs e) => AtualizarAbertos();

        private void BtnAddApp_Click(object sender, RoutedEventArgs e) => AdicionarApp(CmbAbertos.SelectedItem as string);

        private void BtnProcurarApp_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Programas (*.exe)|*.exe", Title = "Escolha o programa que deve fechar ao ativar um modo" };
            if (dlg.ShowDialog(this) == true) AdicionarApp(Path.GetFileNameWithoutExtension(dlg.FileName));
        }

        private void AdicionarApp(string nome)
        {
            if (string.IsNullOrWhiteSpace(nome) || nome == "Carregando…") return;
            if (ProcessService.EhProtegido(nome)) { ToastWindow.Mostrar("Esse é do sistema", "Não dá para adicionar (o Windows precisa dele).", "⚠"); return; }
            ConfigService.Perfil.AppsMonitorados[nome] = true;
            ConfigService.Salvar(true);
            ToastWindow.Mostrar("Adicionado", nome + " vai fechar ao ativar um modo.", "✔");
            AtualizarProgramas();
        }

        // ================= CONTROLE E ATALHOS =================
        private void CarregarControles()
        {
            _carregando = true;
            var c = ConfigService.Atual;
            AtualizarTextoControle();
            ChkGuia.IsChecked = c.AbrirModoTvAoGuia;
            ChkVibrar.IsChecked = c.VibrarControle;
            ChkCombos.IsChecked = c.CombosControle;
            TabelaCombos.Opacity = c.CombosControle ? 1 : 0.45;
            CmbModoPadrao.IsEnabled = c.CombosControle;
            var perfil = ConfigService.Perfil;
            var itens = new List<Opcao> { new Opcao("", "Primeiro modo da lista") };
            itens.AddRange(perfil.Modos.Select(m => new Opcao(m.Id, m.Nome)));
            CmbModoPadrao.ItemsSource = itens;
            CmbModoPadrao.SelectedItem = itens.FirstOrDefault(i => i.Valor == perfil.ModoIdAtalhoPadrao) ?? itens[0];
            EdRestaurar.Text = c.TeclaRestaurar; EdFecharJogo.Text = c.TeclaFecharJogo; EdAtalhoTv.Text = c.TeclaModoTv;
            MontarListaAtalhos();
            _carregando = false;
        }

        private void MontarListaAtalhos()
        {
            ListaAtalhos.Children.Clear();
            var c = ConfigService.Atual;
            var linhas = new List<(string tecla, string acao)>();
            foreach (var m in ConfigService.Perfil.Modos.Where(m => !string.IsNullOrWhiteSpace(m.TeclaAtalho))) linhas.Add((m.TeclaAtalho, "Ativar “" + m.Nome + "”"));
            if (!string.IsNullOrWhiteSpace(c.TeclaRestaurar)) linhas.Add((c.TeclaRestaurar, "Restaurar desktop"));
            if (!string.IsNullOrWhiteSpace(c.TeclaFecharJogo)) linhas.Add((c.TeclaFecharJogo, "Fechar jogo travado"));
            if (!string.IsNullOrWhiteSpace(c.TeclaModoTv)) linhas.Add((c.TeclaModoTv, "Abrir Modo TV"));
            if (linhas.Count == 0) { ListaAtalhos.Children.Add(new TextBlock { Text = "Nenhum atalho de teclado definido.", Style = (Style)FindResource("Sub") }); return; }
            var falhas = App.UltimasFalhasAtalhos;
            foreach (var (tecla, acao) in linhas)
            {
                bool falhou = falhas.Any(f => f.StartsWith(tecla + " ", StringComparison.OrdinalIgnoreCase));
                var d = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
                var chip = Chip("⌨ " + tecla); chip.Width = 150; chip.Margin = new Thickness(0, 0, 12, 0);
                DockPanel.SetDock(chip, Dock.Left);
                d.Children.Add(chip);
                var t = new TextBlock { Text = acao + (falhou ? "   ⚠ em uso por outro programa — escolha outra tecla" : ""), VerticalAlignment = VerticalAlignment.Center };
                if (falhou) t.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
                d.Children.Add(t);
                ListaAtalhos.Children.Add(d);
            }
        }

        private void ChkControle_Click(object sender, RoutedEventArgs e)
        {
            if (_carregando) return;
            var c = ConfigService.Atual;
            c.AbrirModoTvAoGuia = ChkGuia.IsChecked == true;
            c.VibrarControle = ChkVibrar.IsChecked == true;
            c.CombosControle = ChkCombos.IsChecked == true;
            if (App.Controle != null) App.Controle.VibracaoAtiva = c.VibrarControle;
            ConfigService.Salvar(true);
            TabelaCombos.Opacity = c.CombosControle ? 1 : 0.45;
            CmbModoPadrao.IsEnabled = c.CombosControle;
            if (sender == ChkVibrar && c.VibrarControle) App.Controle?.Vibrar(160);
        }

        private void CmbModoPadrao_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_carregando || CmbModoPadrao.SelectedItem is not Opcao o) return;
            ConfigService.Perfil.ModoIdAtalhoPadrao = o.Valor;
            ConfigService.Salvar(true);
        }

        private void BtnSalvarAtalhos_Click(object sender, RoutedEventArgs e)
        {
            var campos = new[] { (EdRestaurar, "Restaurar desktop"), (EdFecharJogo, "Fechar jogo travado"), (EdAtalhoTv, "Abrir Modo TV") };
            foreach (var (caixa, nome) in campos)
            {
                var t = caixa.Text.Trim();
                if (t.Length > 0 && !HotkeyService.Analisar(t, out _, out _)) { ToastWindow.Mostrar("Atalho inválido", nome + ": " + t, "⚠"); return; }
                var modo = ConfigService.Perfil.Modos.FirstOrDefault(m => HotkeyService.Iguais(m.TeclaAtalho, t));
                if (modo != null) { ToastWindow.Mostrar("Atalho repetido", $"{t} já ativa o modo “{modo.Nome}”.", "⚠"); return; }
            }
            for (int i = 0; i < campos.Length; i++)
                for (int j = i + 1; j < campos.Length; j++)
                    if (HotkeyService.Iguais(campos[i].Item1.Text.Trim(), campos[j].Item1.Text.Trim()))
                    { ToastWindow.Mostrar("Atalho repetido", $"“{campos[i].Item2}” e “{campos[j].Item2}” estão com a mesma tecla.", "⚠"); return; }

            var c = ConfigService.Atual;
            c.TeclaRestaurar = EdRestaurar.Text.Trim();
            c.TeclaFecharJogo = EdFecharJogo.Text.Trim();
            c.TeclaModoTv = EdAtalhoTv.Text.Trim();
            ConfigService.Salvar(true);
            var falhas = App.RegistrarAtalhos();
            if (falhas.Count > 0) ToastWindow.Mostrar("Alguns atalhos não funcionaram", "Já usados por outro programa: " + string.Join(", ", falhas), "⚠");
            else ToastWindow.Mostrar("Atalhos aplicados", "Já funcionam, até dentro do jogo.", "⌨");
            MontarListaAtalhos();
        }

        // ================= APARÊNCIA =================
        private void CarregarAparencia()
        {
            bool antes = _carregando; _carregando = true;
            var c = ConfigService.Atual;

            PainelTemas.Children.Clear();
            foreach (var (id, nome) in ThemeService.Temas)
            {
                var rb = new RadioButton { Style = (Style)FindResource("Segmento"), GroupName = "tema", Content = nome, Tag = id, IsChecked = ThemeService.TemaAtual == id, MinWidth = 110 };
                rb.Checked += (s, e) =>
                {
                    if (_carregando) return;
                    ConfigService.Atual.Tema = (string)((RadioButton)s).Tag;
                    ThemeService.AplicarTema(ConfigService.Atual.Tema);
                    ConfigService.Salvar(true);
                    AplicarEscurecer();
                };
                PainelTemas.Children.Add(rb);
            }

            PainelAccent.Children.Clear();
            var cores = CoresAccent.ToList();
            if (!cores.Any(h => h.Equals(c.Accent, StringComparison.OrdinalIgnoreCase))) cores.Add(c.Accent);
            foreach (var hex in cores)
            {
                var b = new Button { Style = (Style)FindResource("BolinhaCor"), Width = 36, Height = 36, Margin = new Thickness(0, 0, 12, 8), Tag = hex, Background = new SolidColorBrush(ThemeService.CorDe(hex, Colors.SeaGreen)), ToolTip = hex.ToUpperInvariant() };
                if (hex.Equals(c.Accent, StringComparison.OrdinalIgnoreCase)) b.SetResourceReference(BorderBrushProperty, "SelecaoBrush");
                b.Click += (s, e) => DefinirAccent((string)((Button)s).Tag);
                PainelAccent.Children.Add(b);
            }
            var mais = new Button { Style = (Style)FindResource("BolinhaCor"), Width = 36, Height = 36, Margin = new Thickness(0, 0, 12, 8), Content = "＋", ToolTip = "Escolher qualquer cor…" };
            mais.SetResourceReference(BackgroundProperty, "Surface2Brush");
            mais.SetResourceReference(ForegroundProperty, "TextBrush");
            mais.Click += (s, e) => { var h = EscolherCor(ConfigService.Atual.Accent); if (h != null) DefinirAccent(h); };
            PainelAccent.Children.Add(mais);

            SldDesfoque.Value = c.DesfoqueWallpaper; SldEscurecer.Value = c.TintingWallpaperAtivo ? c.NivelFoscoWallpaper : 0;
            TxtDesfoque.Text = c.DesfoqueWallpaper + "%"; TxtEscurecer.Text = (int)SldEscurecer.Value + "%";
            CarregarMiniatura(ImgThumbJanela, TxtSemWallpaper, c.WallpaperPath);
            CarregarMiniatura(ImgThumbTv, TxtTvMesmo, c.WallpaperFullscreenPath);
            BtnRemoverWallpaper.IsEnabled = !string.IsNullOrWhiteSpace(c.WallpaperPath);
            BtnWallpaperTvMesmo.IsEnabled = !string.IsNullOrWhiteSpace(c.WallpaperFullscreenPath);
            AplicarWallpaper();
            _carregando = antes;
        }

        private void DefinirAccent(string hex)
        {
            ConfigService.Atual.Accent = hex;
            ThemeService.AplicarAccent(hex);
            ConfigService.Salvar(true);
            CarregarAparencia();
        }

        private void CarregarMiniatura(Image img, TextBlock vazio, string caminho)
        {
            img.Source = null;
            vazio.Visibility = Visibility.Visible;
            if (string.IsNullOrWhiteSpace(caminho)) return;
            Task.Run(() => WallpaperService.Carregar(caminho, 0, 360)).ContinueWith(t => Dispatcher.BeginInvoke(new Action(() =>
            {
                img.Source = t.Result;
                vazio.Visibility = t.Result == null ? Visibility.Visible : Visibility.Collapsed;
            })));
        }

        private void AplicarWallpaper()
        {
            var c = ConfigService.Atual;
            AplicarEscurecer();
            if (string.IsNullOrWhiteSpace(c.WallpaperPath)) { Fundo.Source = null; _wpCarregado = ""; return; }
            var caminho = c.WallpaperPath; var desf = c.DesfoqueWallpaper;
            var chave = caminho + "|" + desf;
            if (chave == _wpCarregado && Fundo.Source != null) return;      // MELHORIA 4.1: não recarrega a imagem toda vez que a janela abre
            Task.Run(() => WallpaperService.Carregar(caminho, desf)).ContinueWith(t =>
                Dispatcher.BeginInvoke(new Action(() => { if (t.Result != null) { Fundo.Source = t.Result; _wpCarregado = chave; } })));
        }

        /// <summary>
        /// CORREÇÃO 4.1: a camada "escurecer" era aplicada mesmo sem papel de parede (deixava o fundo acinzentado).
        /// No tema claro ela clareia em vez de escurecer.
        /// </summary>
        private void AplicarEscurecer()
        {
            var c = ConfigService.Atual;
            bool temImagem = !string.IsNullOrWhiteSpace(c.WallpaperPath);
            Escuro.Fill = ThemeService.Escuro ? Brushes.Black : Brushes.White;
            Escuro.Opacity = temImagem && c.TintingWallpaperAtivo ? c.NivelFoscoWallpaper / 100.0 * 0.9 : 0;
        }

        private string EscolherImagem(string titulo)
        {
            // CORREÇÃO 4.1.1: a lista escondia formatos comuns (.jfif de imagens baixadas, .heic do iPhone, .avif, .tif)
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Imagens|*.jpg;*.jpeg;*.jfif;*.png;*.bmp;*.webp;*.gif;*.tif;*.tiff;*.heic;*.heif;*.avif;*.ico|Todos os arquivos|*.*",
                Title = titulo
            };
            if (dlg.ShowDialog(this) != true) return null;
            // CORREÇÃO 4.1.1: antes, se a imagem não abria, nada acontecia e não aparecia aviso nenhum
            if (WallpaperService.Carregar(dlg.FileName, 0, 64) == null)
            {
                var ext = Path.GetExtension(dlg.FileName).ToLowerInvariant();
                var dica = ext is ".heic" or ".heif" or ".avif" or ".webp"
                    ? "O Windows precisa da extensão gratuita desse formato (Microsoft Store: “Extensões de Imagem HEIF/AV1/Webp”), ou converta a foto para JPG/PNG."
                    : "O arquivo pode estar corrompido ou não ser uma imagem. Tente salvar a foto como JPG ou PNG.";
                ToastWindow.Mostrar("Não consegui abrir essa imagem", dica, "⚠");
                return null;
            }
            return dlg.FileName;
        }

        private void BtnWallpaper_Click(object sender, RoutedEventArgs e)
        {
            var f = EscolherImagem("Escolha o papel de parede da janela");
            if (f == null) return;
            ConfigService.Atual.WallpaperPath = WallpaperService.Importar(f, "janela");
            ConfigService.Atual.TintingWallpaperAtivo = true;
            ConfigService.Salvar(true);
            WallpaperService.LimparNaoUsados();
            _wpCarregado = "";
            CarregarAparencia();
            ToastWindow.Mostrar("Papel de parede aplicado", "Ajuste o desfoque e o escurecer logo abaixo se quiser a imagem mais nítida.", "🖼");
        }

        private void BtnRemoverWallpaper_Click(object sender, RoutedEventArgs e)
        {
            ConfigService.Atual.WallpaperPath = ""; ConfigService.Salvar(true);
            WallpaperService.LimparNaoUsados();
            CarregarAparencia();
        }

        private void BtnWallpaperTv_Click(object sender, RoutedEventArgs e)
        {
            var f = EscolherImagem("Escolha o papel de parede do Modo TV");
            if (f == null) return;
            ConfigService.Atual.WallpaperFullscreenPath = WallpaperService.Importar(f, "tv");
            ConfigService.Salvar(true);
            WallpaperService.LimparNaoUsados();
            CarregarAparencia();
        }

        private void BtnWallpaperTvMesmo_Click(object sender, RoutedEventArgs e)
        {
            ConfigService.Atual.WallpaperFullscreenPath = ""; ConfigService.Salvar(true);
            WallpaperService.LimparNaoUsados();
            CarregarAparencia();
        }

        private void SldDesfoque_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtDesfoque == null || _carregando) return;
            ConfigService.Atual.DesfoqueWallpaper = (int)e.NewValue; TxtDesfoque.Text = (int)e.NewValue + "%";
            ConfigService.Salvar();                 // com atraso: não grava a cada tique do slider
            _wpTimer.Stop(); _wpTimer.Start();
        }

        private void SldEscurecer_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtEscurecer == null || _carregando) return;
            var c = ConfigService.Atual;
            c.NivelFoscoWallpaper = (int)e.NewValue; TxtEscurecer.Text = (int)e.NewValue + "%";
            c.TintingWallpaperAtivo = true;          // CORREÇÃO 4.1: o slider não fazia nada se o escurecimento estivesse desligado no config
            AplicarEscurecer();
            ConfigService.Salvar();
        }

        // ================= GERAL =================
        private void CarregarGeral()
        {
            _carregando = true;
            var c = ConfigService.Atual;
            var perfil = ConfigService.Perfil;
            EdNomePerfil.Text = perfil.Nome;
            BtnExcluirPerfil.IsEnabled = c.Perfis.Count > 1;
            BtnExcluirPerfil.ToolTip = c.Perfis.Count > 1 ? null : "É preciso ter pelo menos um perfil";

            ChkWindows.IsChecked = c.IniciarComWindows; ChkMinimizado.IsChecked = c.IniciarMinimizado;
            ChkMinimizado.IsEnabled = c.IniciarComWindows;
            ChkAvisos.IsChecked = c.MostrarAvisos;
            ChkNotificacoes.IsChecked = c.SilenciarNotificacoes;

            var telas = new List<Opcao> { new Opcao("escolher", "Escolher quais telas acender"), new Opcao("auto", "Automático (telas do “Jogar no Monitor” ou todas)"), new Opcao("todas", "Acender todas as telas"), new Opcao("manter", "Não mexer nas telas") };
            telas.AddRange(perfil.Modos.Where(m => m.Tela != "manter").Select(m => new Opcao("modo:" + m.Id, "Usar as telas do modo “" + m.Nome + "”")));
            CmbRestTelas.ItemsSource = telas;
            CmbRestTelas.SelectedItem = telas.FirstOrDefault(o => o.Valor == c.RestaurarTelas) ?? telas[0];
            var aud = new List<Opcao> { new Opcao("A", "Voltar para a saída A"), new Opcao("B", "Voltar para a saída B"), new Opcao("nenhum", "Não mexer no áudio") };
            CmbRestAudio.ItemsSource = aud;
            CmbRestAudio.SelectedItem = aud.FirstOrDefault(o => o.Valor == c.RestaurarAudio) ?? aud[0];

            TxtVersao.Text = $"GameModeHub {AppInfo.Versao} · {AppInfo.Edicao}";
            if (string.IsNullOrEmpty(_urlUpdate)) TxtUpdate.Text = c.UltimaVerificacaoUpdate > DateTime.MinValue ? "Última verificação: " + c.UltimaVerificacaoUpdate.ToString("dd/MM 'às' HH:mm") : "Ainda não verificado.";
            ChkUpdates.IsChecked = c.VerificarAtualizacoes;

            EdPorta.Text = c.Porta.ToString();
            bool admin = SystemService.EhAdmin();
            TxtAdmin.Text = admin ? "Rodando como administrador" : "Rodando sem administrador (recomendado)";
            BtnAdmin.Visibility = admin ? Visibility.Collapsed : Visibility.Visible;
            TxtDados.Text = "Dados: " + Paths.DataDir + (Paths.Portable ? "  (modo portátil)" : "");
            _carregando = false;
            CarregarSaidas();
            CarregarTelasRestaurar();
        }

        // ---- Telas ao restaurar (NOVO 4.1.1) ----
        private async void CarregarTelasRestaurar()
        {
            bool mostrar = ConfigService.Atual.RestaurarTelas == "escolher";
            BlocoTelasRestaurar.Visibility = mostrar ? Visibility.Visible : Visibility.Collapsed;
            if (!mostrar) return;
            PainelTelasRestaurar.Children.Clear();
            PainelTelasRestaurar.Children.Add(new TextBlock { Text = "Procurando telas…", Style = (Style)FindResource("Sub") });
            var telas = await Task.Run(() => DisplayService.Listar());
            var lay = GarantirLayoutRestaurar(telas);
            PainelTelasRestaurar.Children.Clear();

            CheckBox Interruptor(string path, string nome, string info, bool marcado)
            {
                var cb = new CheckBox
                {
                    Style = (Style)FindResource("Interruptor"), Tag = new[] { path, nome }, Margin = new Thickness(0, 0, 0, 10), IsChecked = marcado,
                    Content = new TextBlock { Inlines = { new System.Windows.Documents.Run(nome) { FontWeight = FontWeights.SemiBold }, new System.Windows.Documents.Run("   " + info) { Foreground = (Brush)FindResource("MutedBrush"), FontSize = 12 } } }
                };
                cb.Click += TelaRestaurar_Click;
                return cb;
            }
            foreach (var t in telas)
                PainelTelasRestaurar.Children.Add(Interruptor(t.Path, t.Nome, (t.Ativa ? "acesa agora" : "apagada agora") + (t.Principal ? " · principal" : ""),
                    lay.TelasAtivas.Contains(t.Path, StringComparer.OrdinalIgnoreCase)));
            // telas escolhidas que não estão conectadas agora continuam na lista para não perder a escolha
            for (int i = 0; i < lay.TelasAtivas.Count; i++)
            {
                var p = lay.TelasAtivas[i];
                if (telas.Any(t => string.Equals(t.Path, p, StringComparison.OrdinalIgnoreCase))) continue;
                PainelTelasRestaurar.Children.Add(Interruptor(p, i < lay.NomesAtivos.Count ? lay.NomesAtivos[i] : "Tela", "desconectada agora (TV desligada?)", true));
            }
            if (PainelTelasRestaurar.Children.Count == 0)
                PainelTelasRestaurar.Children.Add(new TextBlock { Text = "Nenhuma tela encontrada.", Style = (Style)FindResource("Sub") });
            AtualizarTextoTelasRestaurar(lay);
        }

        /// <summary>Na primeira vez, começa com as telas que estão acesas agora.</summary>
        private static LayoutTelas GarantirLayoutRestaurar(List<TelaInfo> telas)
        {
            var lay = ConfigService.Layouts.FirstOrDefault(l => l.Id == ModeService.IdLayoutRestaurar);
            if (lay != null && lay.TelasAtivas.Count > 0) return lay;
            var base_ = telas.Where(t => t.Ativa).ToList();
            if (base_.Count == 0) base_ = telas;
            if (lay == null) { lay = new LayoutTelas { Id = ModeService.IdLayoutRestaurar, Nome = "Ao restaurar" }; ConfigService.Layouts.Add(lay); }
            lay.TelasAtivas = base_.Select(t => t.Path).ToList();
            lay.NomesAtivos = base_.Select(t => t.Nome).ToList();
            lay.PrincipalPath = base_.FirstOrDefault(t => t.Principal)?.Path ?? lay.TelasAtivas.FirstOrDefault() ?? "";
            ConfigService.Salvar(true);
            return lay;
        }

        private void TelaRestaurar_Click(object sender, RoutedEventArgs e)
        {
            var marc = PainelTelasRestaurar.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).ToList();
            if (marc.Count == 0)
            {
                ((CheckBox)sender).IsChecked = true;
                ToastWindow.Mostrar("Deixe pelo menos uma tela acesa", "Senão o desktop ficaria sem nenhuma tela.", "⚠");
                return;
            }
            var lay = ConfigService.Layouts.FirstOrDefault(l => l.Id == ModeService.IdLayoutRestaurar);
            if (lay == null) { lay = new LayoutTelas { Id = ModeService.IdLayoutRestaurar, Nome = "Ao restaurar" }; ConfigService.Layouts.Add(lay); }
            lay.TelasAtivas = marc.Select(c => ((string[])c.Tag)[0]).ToList();
            lay.NomesAtivos = marc.Select(c => ((string[])c.Tag)[1]).ToList();
            lay.PrincipalPath = lay.TelasAtivas[0];
            ConfigService.Salvar(true);
            AtualizarTextoTelasRestaurar(lay);
        }

        private void AtualizarTextoTelasRestaurar(LayoutTelas lay)
        {
            var apagadas = PainelTelasRestaurar.Children.OfType<CheckBox>().Where(c => c.IsChecked != true).Select(c => ((string[])c.Tag)[1]).ToList();
            TxtTelasRestaurar.Text = "✔ Ao restaurar, ficam acesas: " + string.Join(" + ", lay.NomesAtivos) +
                                     (apagadas.Count > 0 ? "   ·   apagadas: " + string.Join(" + ", apagadas) : "");
        }

        private void BtnAtualizarTelasRestaurar_Click(object sender, RoutedEventArgs e) => CarregarTelasRestaurar();

        private void BtnTelasRestaurarAgora_Click(object sender, RoutedEventArgs e)
        {
            var lay = ConfigService.Layouts.FirstOrDefault(l => l.Id == ModeService.IdLayoutRestaurar);
            if (lay != null) lay.TelasAtivas.Clear();     // GarantirLayoutRestaurar refaz com as telas acesas agora
            CarregarTelasRestaurar();
        }

        private async void CarregarSaidas()
        {
            TxtSaidasInfo.Text = "Procurando aparelhos de som…";
            var saidas = await Task.Run(() => AudioService.ListarSaidas());
            var c = ConfigService.Atual;
            _carregando = true;
            CmbSaidaA.ItemsSource = saidas; CmbSaidaB.ItemsSource = saidas;
            var a = AudioService.Resolver(c.AudioSaidaACmd, c.AudioSaidaANome, saidas);
            var b = AudioService.Resolver(c.AudioSaidaBCmd, c.AudioSaidaBNome, saidas);
            CmbSaidaA.SelectedItem = a != null ? saidas.FirstOrDefault(s => s.Id == a.Id) : null;
            CmbSaidaB.SelectedItem = b != null ? saidas.FirstOrDefault(s => s.Id == b.Id) : null;
            _carregando = false;
            TxtSaidasInfo.Text = saidas.Count == 0 ? "⚠ Nenhum aparelho de som ativo encontrado."
                : $"{saidas.Count} aparelho(s) encontrado(s). Use “Testar” para saber qual é qual.";
        }

        private void BtnAtualizarSaidas_Click(object sender, RoutedEventArgs e) => CarregarSaidas();

        private void CmbSaida_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_carregando) return;
            var cb = (ComboBox)sender; var s = cb.SelectedItem as SaidaAudio; if (s == null) return;
            var c = ConfigService.Atual;
            if ((string)cb.Tag == "A") { c.AudioSaidaACmd = s.Id; c.AudioSaidaANome = s.Nome; } else { c.AudioSaidaBCmd = s.Id; c.AudioSaidaBNome = s.Nome; }
            ConfigService.Salvar(true);
            TxtSaidasInfo.Text = $"✔ Saída {(string)cb.Tag}: {s.Nome}";
        }

        private void BtnTestarSaida_Click(object sender, RoutedEventArgs e)
        {
            var slot = (string)((Button)sender).Tag;
            var s = (slot == "A" ? CmbSaidaA : CmbSaidaB).SelectedItem as SaidaAudio;
            if (s == null) { ToastWindow.Mostrar("Escolha um aparelho primeiro", "Selecione na lista da saída " + slot + ".", "⚠"); return; }
            var btn = (Button)sender; btn.IsEnabled = false;
            Task.Run(() => AudioService.Testar(s.Id)).ContinueWith(t => Dispatcher.BeginInvoke(new Action(() =>
            {
                btn.IsEnabled = true;
                if (!t.Result) ToastWindow.Mostrar("Não consegui tocar o som", s.Nome + " pode estar ocupado por outro programa.", "⚠");
            })));
        }

        private void CmbRestaurar_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_carregando) return;
            var c = ConfigService.Atual;
            if (CmbRestTelas.SelectedItem is Opcao t) c.RestaurarTelas = t.Valor;
            if (CmbRestAudio.SelectedItem is Opcao a) c.RestaurarAudio = a.Valor;
            ConfigService.Salvar(true);
            if (sender == CmbRestTelas) CarregarTelasRestaurar();
        }

        private void ChkNotificacoes_Click(object sender, RoutedEventArgs e) { ConfigService.Atual.SilenciarNotificacoes = ChkNotificacoes.IsChecked == true; ConfigService.Salvar(true); }

        private void BtnRenomearPerfil_Click(object sender, RoutedEventArgs e)
        {
            var nome = EdNomePerfil.Text.Trim();
            if (nome.Length == 0) { ToastWindow.Mostrar("Digite um nome", null, "⚠"); return; }
            ConfigService.Perfil.Nome = nome; ConfigService.Salvar(true);
            CarregarPerfis();
            ToastWindow.Mostrar("Perfil renomeado", nome, "👤");
        }

        private void BtnNovoPerfil_Click(object sender, RoutedEventArgs e)
        {
            var chave = ConfigService.DuplicarPerfil(ConfigService.Atual.PerfilAtivo, ConfigService.Perfil.Nome + " 2");
            ConfigService.TrocarPerfil(chave);
            NavGeral.IsChecked = true; MostrarPagina("geral");
            EdNomePerfil.Focus(); EdNomePerfil.SelectAll();
            ToastWindow.Mostrar("Perfil criado", "É uma cópia do anterior. Dê um nome e ajuste os modos.", "👤");
        }

        private void BtnExcluirPerfil_Click(object sender, RoutedEventArgs e)
        {
            var nome = ConfigService.Perfil.Nome;
            if (MessageBox.Show(this, $"Excluir o perfil “{nome}” com todos os modos dele?\n\nIsso não pode ser desfeito.", "GameModeHub", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            if (ConfigService.ExcluirPerfil(ConfigService.Atual.PerfilAtivo)) ToastWindow.Mostrar("Perfil excluído", nome, "🗑");
        }

        private void ChkWindows_Click(object sender, RoutedEventArgs e)
        {
            ConfigService.Atual.IniciarComWindows = ChkWindows.IsChecked == true;
            SystemService.DefinirIniciarComWindows(ConfigService.Atual.IniciarComWindows);
            ConfigService.Salvar(true);
            ChkMinimizado.IsEnabled = ConfigService.Atual.IniciarComWindows;
        }

        private void ChkMinimizado_Click(object sender, RoutedEventArgs e) { ConfigService.Atual.IniciarMinimizado = ChkMinimizado.IsChecked == true; ConfigService.Salvar(true); }
        private void ChkAvisos_Click(object sender, RoutedEventArgs e) { ConfigService.Atual.MostrarAvisos = ChkAvisos.IsChecked == true; ConfigService.Salvar(true); }

        private void BtnExportar_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "Backup do GameModeHub (*.json)|*.json", FileName = $"GameModeHub-backup-{DateTime.Now:yyyy-MM-dd}.json", Title = "Salvar backup" };
            if (dlg.ShowDialog(this) != true) return;
            try { ConfigService.ExportarBackup(dlg.FileName); ToastWindow.Mostrar("Backup salvo", Path.GetFileName(dlg.FileName), "⬇"); }
            catch (Exception ex) { Log.Error("Exportar", ex); ToastWindow.Mostrar("Não consegui salvar", ex.Message, "⚠"); }
        }

        private void BtnImportar_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Backup do GameModeHub (*.json)|*.json|Todos os arquivos|*.*", Title = "Escolha o backup" };
            if (dlg.ShowDialog(this) != true) return;
            if (MessageBox.Show(this, "Importar vai substituir perfis, modos, telas, atalhos e aparência por os do arquivo.\nOs celulares pareados deste PC são mantidos.\n\nContinuar?", "GameModeHub", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            var erro = ConfigService.ImportarBackup(dlg.FileName);
            if (erro != null) { ToastWindow.Mostrar("Não consegui importar", erro, "⚠"); return; }
            var c = ConfigService.Atual;
            ThemeService.AplicarTema(c.Tema); ThemeService.AplicarAccent(c.Accent);
            SystemService.DefinirIniciarComWindows(c.IniciarComWindows);
            if (App.Controle != null) App.Controle.VibracaoAtiva = c.VibrarControle;
            _wpCarregado = "";
            CarregarAparencia();
            ToastWindow.Mostrar("Backup importado", "Tudo foi restaurado.", "⬆");
            NavGeral.IsChecked = true; MostrarPagina("geral");
        }

        private void ChkUpdates_Click(object sender, RoutedEventArgs e) { ConfigService.Atual.VerificarAtualizacoes = ChkUpdates.IsChecked == true; ConfigService.Salvar(true); }

        private async void BtnVerificarUpdate_Click(object sender, RoutedEventArgs e)
        {
            BtnVerificarUpdate.IsEnabled = false;
            TxtUpdate.Text = "Verificando…";
            var r = await UpdateService.Verificar();
            BtnVerificarUpdate.IsEnabled = true;
            if (r.Ok) { ConfigService.Atual.UltimaVerificacaoUpdate = DateTime.Now; ConfigService.Salvar(); }
            MostrarResultadoUpdate(r, true);
        }

        public void MostrarResultadoUpdate(UpdateService.Resultado r, bool manual)
        {
            if (r.TemNova)
            {
                _urlUpdate = r.Url;
                TxtUpdate.Text = $"🎉 Nova versão {r.Versao} disponível!";
                BtnBaixarUpdate.Visibility = Visibility.Visible;
            }
            else if (manual)
            {
                TxtUpdate.Text = !r.Ok ? "⚠ " + r.Erro : string.IsNullOrEmpty(r.Erro) ? "✔ Você já está na versão mais recente." : r.Erro;
            }
        }

        private void BtnBaixarUpdate_Click(object sender, RoutedEventArgs e) { if (!string.IsNullOrEmpty(_urlUpdate)) ProcessService.Abrir(_urlUpdate); }

        private void BtnAplicarPorta_Click(object sender, RoutedEventArgs e)
        {
            var c = ConfigService.Atual;
            if (!int.TryParse(EdPorta.Text.Trim(), out int porta) || porta < 1024 || porta > 65535)
            { ToastWindow.Mostrar("Porta inválida", "Use um número entre 1024 e 65535 (padrão: 5050).", "⚠"); EdPorta.Text = c.Porta.ToString(); return; }
            if (porta == c.Porta) { ToastWindow.Mostrar("Nada mudou", "O servidor já está na porta " + porta + ".", "ℹ"); return; }
            c.Porta = porta;
            ConfigService.Salvar(true);
            App.ReiniciarServidor();
            if (App.Servidor.Rodando) ToastWindow.Mostrar("Porta alterada para " + porta, "Libere o firewall e pareie o celular de novo (tela Celular).", "✔");
            else ToastWindow.Mostrar("Não consegui abrir a porta " + porta, App.Servidor.Erro, "⚠");
        }

        private void AtualizarPerf()
        {
            var s = StatsService.Ler();
            if (_pagina == "inicio")
            {
                TxtCpu.Text = $"CPU {s.cpu}%";
                TxtRam.Text = $"RAM {s.ramUsadaGb:0.0}/{s.ramTotalGb:0} GB";
                if (!_volTimer.IsEnabled && !SldVolume.IsMouseCaptureWithin) AtualizarAudioInicio();   // acompanha o volume mudado por fora
            }
            else if (_pagina == "geral")
            {
                using var p = Process.GetCurrentProcess();
                TxtPerf.Text = $"GameModeHub usa {p.WorkingSet64 / 1048576} MB de RAM  ·  PC: CPU {s.cpu}%  ·  RAM {s.ramUsadaGb:0.0}/{s.ramTotalGb:0.0} GB";
            }
        }

        private void BtnAdmin_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show(this, "O GameModeHub vai fechar e abrir de novo como administrador (o Windows vai pedir permissão).\n\nSó é necessário se algum jogo/programa aberto como administrador não estiver fechando.", "GameModeHub", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
            SystemService.ReiniciarComoAdmin();
        }

        private void BtnLog_Click(object sender, RoutedEventArgs e) { if (File.Exists(Paths.LogFile)) ProcessService.Abrir(Paths.LogFile); else ToastWindow.Mostrar("Ainda não há log", null, "ℹ"); }
        private void BtnPasta_Click(object sender, RoutedEventArgs e) => ProcessService.Abrir(Paths.DataDir);
        private void BtnSair_Click(object sender, RoutedEventArgs e) => App.Sair();
    }
}
