using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GameModeHub.Core;
using GameModeHub.Services;

namespace GameModeHub.Views
{
    /// <summary>Tela cheia para usar na TV, só com o controle.</summary>
    public partial class TvWindow : Window
    {
        private readonly DispatcherTimer _relogio = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        private readonly Action<Botao> _handler;
        private readonly Action<string> _aoTrabalhar;
        private readonly Action<string> _aoMudarModo;

        public TvWindow()
        {
            InitializeComponent();

            var c = ConfigService.Atual;
            // NOVO 4.1: papel de parede próprio do Modo TV (se não escolher, usa o mesmo da janela)
            var wp = string.IsNullOrWhiteSpace(c.WallpaperFullscreenPath) ? c.WallpaperPath : c.WallpaperFullscreenPath;
            Escuro.Fill = ThemeService.Escuro ? Brushes.Black : Brushes.White;
            Escuro.Opacity = string.IsNullOrWhiteSpace(wp) ? 0 : Math.Max(0.35, c.NivelFoscoWallpaper / 100.0 * 0.9);
            if (!string.IsNullOrWhiteSpace(wp))
                Task.Run(() => WallpaperService.Carregar(wp, c.DesfoqueWallpaper))
                    .ContinueWith(t => Dispatcher.BeginInvoke(new Action(() => { if (t.Result != null) Fundo.Source = t.Result; })));

            _handler = b => GamepadNav.Tratar(this, b, () => Close());
            if (App.Controle != null) App.Controle.BotaoPressionado += _handler;

            _aoMudarModo = _ => Dispatcher.BeginInvoke(new Action(Montar));
            _aoTrabalhar = t => Dispatcher.BeginInvoke(new Action(() => MostrarTrabalhando(t)));
            ModeService.ModoAtivoMudou += _aoMudarModo;
            ModeService.Trabalhando += _aoTrabalhar;

            _relogio.Tick += (s, e) => AtualizarHora();
            SourceInitialized += (s, e) => ThemeService.AplicarBarraTitulo(this);
            Loaded += (s, e) => { Montar(); AtualizarHora(); _relogio.Start(); Activate(); Focus(); FocarPrimeiro(); };
            // desliga todos os eventos ao fechar (a janela fechada não fica presa na memória)
            Closed += (s, e) =>
            {
                _relogio.Stop();
                if (App.Controle != null) App.Controle.BotaoPressionado -= _handler;
                ModeService.ModoAtivoMudou -= _aoMudarModo;
                ModeService.Trabalhando -= _aoTrabalhar;
            };
            KeyDown += (s, e) => { if (e.Key == Key.Escape) Close(); };
        }

        private void AtualizarHora()
        {
            TxtHora.Text = DateTime.Now.ToString("HH:mm");
            var partes = new System.Collections.Generic.List<string>();
            int vol = AudioService.LerVolume();
            if (vol >= 0) partes.Add((AudioService.LerMudo() ? "🔇 " : "🔊 ") + vol + "%");
            if (App.Controle != null && App.Controle.Conectado)
                partes.Add(App.Controle.Bateria >= 0 ? "🎮 " + App.Controle.Bateria + "%" : "🎮");
            TxtInfo.Text = string.Join("   ", partes);
        }

        private void FocarPrimeiro()
        {
            var ativo = PainelModos.Children.OfType<Button>().FirstOrDefault(b => (string)b.Tag == ModeService.ModoAtivoId);
            if (ativo != null) ativo.Focus();
            else if (PainelModos.Children.Count > 0 && PainelModos.Children[0] is UIElement u) u.Focus();
            else BtnAudio.Focus();
        }

        private void MostrarTrabalhando(string texto)
        {
            bool ocupado = texto != null;
            PainelTrabalhando.Visibility = ocupado ? Visibility.Visible : Visibility.Collapsed;
            PainelModos.Visibility = ocupado ? Visibility.Hidden : Visibility.Visible;
            TxtTrabalhando.Text = texto ?? "";
        }

        private void Montar()
        {
            var ativo = ConfigService.Perfil.Modos.FirstOrDefault(m => m.Id == ModeService.ModoAtivoId);
            TxtEstado.Text = (ativo != null ? "Modo atual: " + ativo.Nome : "Desktop normal") + "   ·   Perfil: " + ConfigService.Perfil.Nome;
            bool tinhaFoco = PainelModos.IsKeyboardFocusWithin;
            PainelModos.Children.Clear();
            foreach (var m in ConfigService.Perfil.Modos)
            {
                var cor = ThemeService.CorDe(m.Cor, Colors.SeaGreen);
                var pilha = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                var bola = new Border { Width = 56, Height = 56, CornerRadius = new CornerRadius(18), Background = new SolidColorBrush(Color.FromArgb(0x40, cor.R, cor.G, cor.B)), Margin = new Thickness(0, 0, 0, 16), HorizontalAlignment = HorizontalAlignment.Center };
                bola.Child = new System.Windows.Shapes.Ellipse { Width = 20, Height = 20, Fill = new SolidColorBrush(cor) };
                pilha.Children.Add(bola);
                pilha.Children.Add(new TextBlock { Text = m.Nome, FontSize = 28, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap });
                if (m.Id == ModeService.ModoAtivoId)
                    pilha.Children.Add(new TextBlock { Text = "● ATIVO", FontSize = 15, FontWeight = FontWeights.Bold, Foreground = (Brush)FindResource("AccentBrush"), TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 10, 0, 0) });

                var b = new Button { Style = (Style)FindResource("CartaoModo"), Content = pilha, Width = 320, Height = 220, Margin = new Thickness(14), Tag = m.Id, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
                if (m.Id == ModeService.ModoAtivoId) { b.BorderBrush = new SolidColorBrush(cor); b.BorderThickness = new Thickness(3); }
                var id = m.Id;
                b.Click += async (s, e) =>
                {
                    var r = await Task.Run(() => ModeService.Ativar(id));
                    if (r.Ok) Close(); else ToastWindow.Mostrar("Algo não saiu como esperado", r.Resumo, "⚠");
                };
                PainelModos.Children.Add(b);
            }
            if (tinhaFoco) FocarPrimeiro();
        }

        private void BtnAudio_Click(object sender, RoutedEventArgs e) =>
            Task.Run(() => AudioService.Alternar()).ContinueWith(t =>
            {
                ToastWindow.Mostrar("Áudio", t.Result ?? "Saída não encontrada — configure em Geral → Saídas de áudio", t.Result != null ? "🔊" : "⚠");
                Dispatcher.BeginInvoke(new Action(AtualizarHora));
            });

        private async void BtnRestaurar_Click(object sender, RoutedEventArgs e)
        {
            var r = await Task.Run(() => ModeService.Restaurar());
            if (r.Ok) Close(); else ToastWindow.Mostrar("Restaurar", r.Resumo, "⚠");
        }

        private void BtnFechar_Click(object sender, RoutedEventArgs e) => Close();
    }
}
