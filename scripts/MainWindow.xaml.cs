using SharpHook;
using SharpHook.Data;
using SharpHook.Native;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

using HookMouseButton = SharpHook.Data.MouseButton;

namespace RustRecoilControl
{
    public partial class MainWindow : Window
    {
        private readonly TaskPoolGlobalHook _hook;
        private readonly Dictionary<string, WeaponProfile> _weaponProfiles;
        private WeaponProfile _currentWeapon;
        private readonly ProcessMonitor _processMonitor;

        private bool _isShooting;
        private bool _isAiming;
        private bool _isCrouching;
        private bool _isEnabled;
        private bool _isWindowVisible = true;
        private CancellationTokenSource _recoilCancellation;
        private int _currentPatternIndex;
        private Window _overlayWindow;
        private DispatcherTimer _overlayTimer;

        private readonly Dictionary<string, float> _sightModifiers = new Dictionary<string, float>
        {
            { "Iron Sight", 0.95f },   // -5%
            { "Holo Sight", 0.90f },   // -10%
            { "8x Sight", 1.50f },     // +50%
            { "16x Sight", 1.50f }     // +50%
        };

        private readonly Dictionary<string, float> _muzzleModifiers = new Dictionary<string, float>
        {
            { "None", 1.0f },
            { "Silencer", 0.95f }, // -5%
            { "Muzzle Boost", 1.0f }, 
            { "Muzzle Brake", 0.50f }  // -50%
        };

        private string GenerateRandomTitle(int length)
        {
            const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
            var random = new Random();
            var sb = new StringBuilder();

            for (int i = 0; i < length; i++)
            {
                sb.Append(chars[random.Next(chars.Length)]);
            }

            return sb.ToString();
        }
        private void Tab_Checked(object sender, RoutedEventArgs e)
        {
            WeaponPanel.Visibility = Visibility.Collapsed;
            AttachmentsPanel.Visibility = Visibility.Collapsed;
            RecoilPanel.Visibility = Visibility.Collapsed;
            SensitivityPanel.Visibility = Visibility.Collapsed;

            if (WeaponTab.IsChecked == true)
                WeaponPanel.Visibility = Visibility.Visible;
            else if (AttachmentsTab.IsChecked == true)
                AttachmentsPanel.Visibility = Visibility.Visible;
            else if (RecoilTab.IsChecked == true)
                RecoilPanel.Visibility = Visibility.Visible;
            else if (SensitivityTab.IsChecked == true)
                SensitivityPanel.Visibility = Visibility.Visible;

        }


        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }
        public MainWindow()
        {
            InitializeComponent();
            WeaponTab.Checked += Tab_Checked;
            AttachmentsTab.Checked += Tab_Checked;
            RecoilTab.Checked += Tab_Checked;
            SensitivityTab.Checked += Tab_Checked;
            Title = GenerateRandomTitle(25);
            _weaponProfiles = new Dictionary<string, WeaponProfile>();
            InitializeWeaponPatterns();

            _processMonitor = new ProcessMonitor();
            _recoilCancellation = new CancellationTokenSource();

            ControlXSlider.ValueChanged += (s, e) =>
            {
                ControlXValue.Text = $"{ControlXSlider.Value:F0}%";
                if (_currentWeapon != null) _currentWeapon.ControlX = (int)ControlXSlider.Value;
            };

            ControlYSlider.ValueChanged += (s, e) =>
            {
                ControlYValue.Text = $"{ControlYSlider.Value:F0}%";
                if (_currentWeapon != null) _currentWeapon.ControlY = (int)ControlYSlider.Value;
            };

            SensitivitySlider.ValueChanged += (s, e) =>
            {
                SensitivityValue.Text = $"{SensitivitySlider.Value:F2}";
                if (_currentWeapon != null) _currentWeapon.Sensitivity = (float)SensitivitySlider.Value;
            };

            AdsSensitivitySlider.ValueChanged += (s, e) =>
            {
                AdsSensitivityValue.Text = $"{AdsSensitivitySlider.Value:F2}";
                if (_currentWeapon != null) _currentWeapon.AdsSensitivity = (float)AdsSensitivitySlider.Value;
            };

            SightComboBox.ItemsSource = _sightModifiers.Keys;
            SightComboBox.SelectedIndex = 0;

            MuzzleComboBox.ItemsSource = _muzzleModifiers.Keys;
            MuzzleComboBox.SelectedIndex = 0;

            _hook = new TaskPoolGlobalHook();
            _hook.KeyPressed += Hook_KeyPressed;
            _hook.KeyReleased += Hook_KeyReleased;
            _hook.MousePressed += Hook_MousePressed;
            _hook.MouseReleased += Hook_MouseReleased;
            _hook.RunAsync();

            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;

            //InitializeOverlay();
        }

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        private void InitializeOverlay()
        {
            _overlayWindow = new Window
            {
                Title = "overlay",
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                Topmost = true,
                ShowInTaskbar = false,
                Width = 300,
                Height = 50
            };

            var textBlock = new TextBlock
            {
                Text = "CT Scripts: 2.2.6",
                Foreground = Brushes.Red,
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(10, 10, 0, 0)
            };

            _overlayWindow.Content = textBlock;
            _overlayTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(16)
            };
            _overlayTimer.Tick += UpdateOverlayPosition;
            _overlayTimer.Start();

            _overlayWindow.Show();
        }

        private void UpdateOverlayPosition(object sender, EventArgs e)
        {
            IntPtr rustWindow = FindWindow(null, "Rust");
            if (rustWindow != IntPtr.Zero)
            {
                if (GetWindowRect(rustWindow, out RECT rect))
                {
                    _overlayWindow.Left = rect.Left;
                    _overlayWindow.Top = rect.Top;
                    _overlayWindow.Visibility = Visibility.Visible;
                }
                else
                {
                    _overlayWindow.Visibility = Visibility.Hidden;
                }
            }
            else
            {
                _overlayWindow.Visibility = Visibility.Hidden;
            }
        }


        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            WeaponComboBox.ItemsSource = _weaponProfiles.Keys;
            WeaponComboBox.SelectedIndex = 0;
            _processMonitor.StartMonitoring("RustClient");
            _processMonitor.ProcessStateChanged += ProcessMonitor_ProcessStateChanged;
        }

        private void MainWindow_Closing(object sender, CancelEventArgs e)
        {
            _recoilCancellation?.Cancel();
            _hook?.Dispose();
            _processMonitor?.StopMonitoring();
        }

        private void InitializeWeaponPatterns()
        {
            var ak47Pattern = new List<RecoilPoint>
            {
                new(-2, 4), new( 0, 3), new( 0, 1), new(-1, 3), new( 0, 5), new( 0, 3), new( 0, 4), new( 0, 3),
                new(-2, 3), new( 0, 2), new( 0, 1), new( 0, 2), new( 0, 4), new( 0, 3), new( 0, 3), new( 0, 3),
                new( 0, 2), new( 0, 1), new( 0, 3), new( 0, 2), new(-1, 3), new( 0, 1), new( 0, 2), new( 0, 3),
                new( 0, 1), new( 0, 2), new( 0, 3), new( 0, 4), new( 0, 3), new(-2, 3), new( 0, 3), new( 0, 3),
                new( 0, 2), new( 0, 1), new( 0, 2), new( 0, 1), new( 0, 2), new( 0, 3), new( 0, 1), new(-1, 3),
                new( 0, 3), new( 0, 2), new( 0, 3), new( 0, 3), new( 0, 4), new( 0, 3), new( 0, 2), new(-2, 1),
                new( 0, 2), new( 0, 1), new( 0, 2), new( 0, 1), new( 0, 2), new( 0, 3), new( 0, 3), new(-1, 3),
                new( 0, 4), new( 0, 5), new(-2, 4), new( 0, 3), new( 0, 2), new( 0, 3), new( 0, 3), new(-1, 1),
                new( 0, 5), new( 0, 3), new(-2, 4), new( 0, 3), new(-1, 5), new(-2, 1), new( 0, 5), new(-1, 3),
                new(-2, 4), new(-3, 5), new(-1, 4), new(-2, 5), new(-3, 4), new(-1, 3), new(-2, 3), new(-1, 3),
                new(-2, 3), new(-1, 3), new(-2, 2), new( 0, 3), new(-1, 1), new(-2, 2), new(-1, 3), new(-2, 4),
                new(-3, 3), new( 0, 3), new(-3, 3), new( 0, 2), new(-1, 1), new(-2, 2), new( 0, 1), new(-1, 2),
                new(-2, 3), new( 0, 1), new(-3, 3), new(-1, 5), new(-3, 4), new(-3, 5), new(-3, 4), new(-3, 3),
                new(-3, 3), new(-2, 2), new(-1, 3), new(-2, 3), new(-1, 1), new(-3, 5), new(-3, 3), new(-3, 3),
                new(-3, 4), new(-2, 3), new(-3, 5), new(-3, 1), new(-1, 3), new(-2, 3), new(-1, 2), new(-2, 1),
                new(-1, 3), new(-2, 2), new(-3, 3), new(-1, 3), new(-2, 1), new(-1, 3), new(-2, 2), new(-3, 3),
                new(-1, 1), new(-2, 2), new(-1, 3), new(-2, 1), new(-1, 2), new(-2, 3), new(-1, 1), new(-2, 3),
                new(-3, 0), new( 0, 3), new(-1, 2), new(-2, 1), new(-3, 2), new( 0, 1), new(-1, 2), new(-2, 1),
                new(-1, 3), new(-2, 2), new(-3, 1), new(-1, 3), new(-2, 3), new(-1, 2), new(-2, 1), new(-4, 3),
                new(-2, 3), new(-3, 3), new(-3, 2), new(-1, 3), new(-2, 1), new(-3, 2), new( 0, 1), new(-1, 2),
                new(-2, 1), new(-1, 2), new(-2, 1), new(-1, 2), new(-2, 1), new(-1, 2), new(-2, 1), new( 0, 2),
                new(-1, 1), new(-2, 2), new(-1, 1), new(-2, 2), new(-1, 1), new(-3, 2), new( 0, 1), new(-2, 3),
                new(-1, 0), new( 0, 2), new(-2, 1), new( 0, 2), new(-1, 1), new(-2, 0), new(-1, 2), new(-2, 1),
                new(-1, 2), new(-2, 1), new( 0, 2), new(-1, 1), new(-2, 2), new(-1, 1), new( 0, 2), new(-2, 1),
                new(-1, 0), new(-2, 3), new(-1, 2), new(-2, 0), new(-1, 3), new(-2, 1), new(-1, 2), new(-2, 1),
                new(-1, 2), new(-3, 3), new( 0, 1), new(-2, 2), new(-1, 1), new(-2, 2), new(-1, 1), new(-2, 2),
                new(-1, 1), new(-2, 2), new(-3, 4), new(-3, 3), new(-1, 3), new(-3, 2), new(-2, 3), new(-3, 3),
                new(-1, 1), new(-2, 3), new(-1, 2), new(-2, 3), new(-3, 1), new(-1, 2), new(-2, 1), new( 0, 2),
                new(-1, 1), new(-2, 2), new(-1, 1), new(-2, 2), new(-1, 3), new(-2, 1), new(-1, 2), new(-2, 3),
                new(-1, 1), new(-2, 2), new(-1, 3), new(-2, 1), new(-3, 3), new(-1, 2), new( 0, 1), new(-2, 2),
                new(-1, 1), new(-2, 0), new( 0, 2), new(-1, 1), new(-2, 2), new(-1, 0), new(-2, 1), new( 0, 2),
                new(-1, 0), new( 0, 1), new(-3, 2), new( 0, 1), new(-3, 2), new(-2, 1), new( 0, 2)
            };

            var HMLGPattern = new List<RecoilPoint>
            {
                new(-7, 7), new(-3, 2), new(-3, 3), new(-3, 3), new(-5, 6),
                new(-7, 4), new(-8, 5), new(-3, 3), new(-3, 4), new(-3, 3),
                new(-3, 3), new(-3, 3), new(-4, 3), new(-3, 5), new(-3, 4),
                new(-6, 5), new(-3, 3), new(-3, 4), new(-3, 3), new(-3, 5),
                new(-3, 3), new(-3, 4), new(-3, 3), new(-5, 5), new(-3, 4),
                new(-3, 5), new(-4, 4), new(-5, 5), new(-4, 6), new(-5, 6),
                new(-6, 7), new(-4, 5), new(-5, 4), new(-6, 6), new(-6, 5),
                new(-3, 4), new(-4, 3), new(-3, 5), new(-5, 4), new(-4, 5),
                new(-6, 6), new(-5, 4), new(-4, 6), new(-5, 6), new(-6, 6),
                new(-6, 6), new(-4, 6), new(-5, 6), new(-4, 5), new(-3, 4),
                new(-5, 5), new(-3, 4), new(-6, 6), new(-4, 6), new(-6, 8),
                new(-5, 7), new(-6, 5), new(-3, 4), new(-4, 6), new(-5, 6),
                new(-3, 6), new(-6, 6), new(-4, 6), new(-3, 6), new(-5, 8),
                new(-3, 6), new(-4, 7), new(-3, 6), new(-3, 6), new(-5, 6),
                new(-6, 8), new(-6, 6), new(-6, 10), new(-7, 8), new(-6, 10),
                new(-8, 12), new(-7, 11), new(-8, 10), new(-6, 11), new(-6, 9),
                new(-4, 6), new(-3, 4), new(-2, 5), new(-3, 4), new(-4, 6),
                new(-3, 6), new(-6, 8), new(-6, 9), new(-5, 9), new(-6, 7),
                new(-3, 6), new(-3, 6), new(-3, 5), new(-1, 4), new(-5, 5),
                new(-1, 4), new(-3, 6), new(-3, 6), new(-5, 8), new(-4, 7),
                new(-5, 9), new(-4, 6), new(-5, 9), new(-4, 9), new(-5, 9),
                new(-4, 8), new(-5, 10), new(-4, 8), new(-3, 9), new(-5, 6),
                new(-3, 4), new(-3, 6), new(-3, 5), new(-3, 6), new(-4, 7),
                new(-5, 8), new(-3, 6), new(-3, 9), new(-4, 7), new(-3, 9),
                new(-3, 8), new(-3, 6), new(-3, 7), new(-2, 6), new(-3, 8),
                new(-3, 6), new(-3, 7), new(-4, 6), new(-5, 9), new(-4, 8),
                new(-5, 9), new(-3, 7), new(-3, 8), new(-1, 6), new(-3, 6),
                new(-2, 3), new(-1, 3), new(-2, 4), new(-3, 8), new(-3, 7),
                new(-3, 11), new(-4, 10), new(-6, 9), new(-5, 11), new(-4, 10),
                new(-6, 14), new(-6, 12), new(-6, 13), new(-6, 11), new(-3, 9),
                new(-5, 6), new(-3, 7), new(-4, 9), new(-3, 5), new(-2, 4),
                new(-1, 3), new(-2, 2), new(0, 1)
            };

            var m249Pattern = new List<RecoilPoint>
            {
                new(-3, 5), new(-3, 3), new(-3, 3), new(-3, 6), new(-3, 3),
                new(-3, 6), new(-3, 5), new(-1, 6), new(-3, 4), new(-3, 5),
                new(-3, 6), new(-2, 4), new(-3, 5), new(-4, 6), new(-2, 6),
                new(-3, 4), new(-3, 6), new(-1, 5), new(-2, 4), new(-4, 6),
                new(-2, 5), new(-3, 6), new(-3, 6), new(-3, 4), new(-4, 8),
                new(-3, 6), new(-5, 7), new(-4, 8), new(-5, 7), new(-4, 8),
                new(-3, 7), new(-2, 5), new(-3, 3), new(-3, 6), new(-3, 6),
                new(-3, 6), new(-3, 6), new(-3, 4), new(-3, 6), new(-3, 5),
                new(-1, 4), new(-3, 5), new(-3, 3), new(-2, 6), new(-3, 4),
                new(-3, 5), new(-3, 4), new(-1, 6), new(-5, 5), new(-1, 4),
                new(-3, 5), new(-3, 6), new(-3, 4), new(-3, 6), new(-5, 8),
                new(-3, 6), new(-3, 7), new(-4, 6), new(-3, 6), new(-2, 5),
                new(-3, 4), new(-1, 5), new(-3, 4), new(-3, 6), new(-5, 6),
                new(-3, 6), new(-3, 6), new(-4, 9), new(-5, 9), new(-6, 11),
                new(-4, 10), new(-6, 9), new(-5, 9), new(-6, 9), new(-3, 9),
                new(-4, 8), new(-5, 10), new(-7, 14), new(-8, 15), new(-7, 13),
                new(-5, 11), new(-4, 7), new(-3, 8), new(-2, 4), new(-1, 3),
                new(-2, 3), new(-1, 3), new(-2, 3), new(-3, 5), new(-3, 6),
                new(-3, 7), new(-4, 8), new(-5, 9), new(-4, 7), new(-5, 8),
                new(-4, 9), new(-3, 6), new(-3, 6), new(-3, 4), new(-3, 3),
                new(-2, 5), new(-1, 4), new(-2, 3), new(-3, 3), new(-1, 6),
                new(-3, 5), new(-3, 4), new(-3, 5), new(-3, 7), new(-3, 6),
                new(-3, 6), new(-3, 5), new(-3, 4), new(-3, 5), new(-2, 4),
                new(-1, 5), new(-3, 4), new(-2, 5), new(-3, 4), new(-1, 3),
                new(-2, 3), new(-3, 3), new(-1, 5), new(-3, 4), new(-3, 5),
                new(-5, 7), new(-3, 8), new(-4, 9), new(-5, 6), new(-4, 7),
                new(-3, 9), new(-5, 6), new(-3, 6), new(-3, 5), new(-1, 4),
                new(-3, 6), new(-5, 6), new(-3, 6), new(-3, 6), new(-3, 6),
                new(-3, 6), new(-3, 3), new(-1, 5), new(-2, 3), new(-1, 3),
                new(-2, 4), new(-1, 2), new(-3, 4), new(-2, 5), new(-3, 4),
                new(-3, 6), new(-3, 8), new(-4, 6), new(-3, 6), new(-3, 4),
                new(-3, 5), new(-2, 4), new(-3, 5), new(-4, 4), new(-3, 6),
                new(-3, 5), new(-3, 6), new(-3, 4), new(-2, 5), new(-3, 4),
                new(-1, 5), new(-3, 3), new(-3, 4), new(-2, 6), new(-4, 5),
                new(-3, 6), new(-3, 4), new(-3, 6), new(-2, 3), new(-3, 5),
                new(-1, 4), new(-3, 5), new(-3, 3), new(-2, 4), new(-3, 6),
                new(-3, 5), new(-3, 3), new(-1, 4), new(-2, 3)
            };


            var mp5Pattern = new List<RecoilPoint>
            {
                new(-15, 13), new(0, 1), new(0, 3), new(-1, 6), new(-5, 9),
                new(-6, 12), new(-6, 8), new(-3, 3), new(-1, 3), new(0, 3),
                new(-2, 3), new(0, 3), new(0, 4), new(0, 6), new(-1, 8),
                new(-3, 7), new(-3, 9), new(-2, 8), new(0, 6), new(0, 3),
                new(0, 3), new(1, 3), new(0, 1), new(-1, 3), new(-3, 6),
                new(-6, 6), new(-1, 5), new(-2, 6), new(0, 3), new(0, 6),
                new(0, 6), new(-3, 6), new(-1, 6), new(-5, 6), new(-4, 3),
                new(-2, 4), new(0, 3), new(-1, 5), new(-2, 6), new(-3, 7),
                new(-7, 9), new(-6, 9), new(-5, 9), new(-1, 6), new(1, 6),
                new(1, 3), new(2, 3), new(0, 2), new(-1, 4), new(-2, 3),
                new(-1, 5), new(0, 3), new(1, 4), new(0, 5), new(0, 4),
                new(-1, 6), new(-5, 6), new(-3, 6), new(0, 6), new(1, 8),
                new(2, 6), new(1, 7), new(-2, 9), new(-6, 9), new(-5, 9),
                new(-4, 6), new(0, 5), new(-2, 7), new(0, 6), new(-3, 6),
                new(-4, 9), new(-3, 9), new(-5, 9), new(-1, 9), new(0, 8),
                new(-5, 7), new(-6, 8), new(-10, 9), new(-8, 6), new(-1, 3),
                new(0, 6), new(0, 4), new(2, 6), new(0, 5), new(0, 6),
                new(-2, 6), new(-3, 6), new(0, 4), new(-2, 5), new(-1, 4),
                new(-5, 6), new(-4, 6), new(-3, 6), new(0, 6), new(0, 6),
                new(2, 8), new(2, 6), new(0, 3), new(1, 3), new(2, 1),
                new(0, 2), new(1, 3), new(0, 1), new(0, 6), new(-2, 6),
                new(-3, 6), new(-2, 5), new(-1, 3), new(0, 1), new(0, 3),
                new(2, 3), new(2, 2), new(0, 1), new(0, 2), new(-7, 6),
                new(-9, 6), new(-6, 6), new(-2, 3), new(0, 4), new(0, 6),
                new(1, 5), new(0, 3), new(0, 3), new(-2, 3), new(0, 1),
                new(0, 2), new(-2, 3), new(0, 3), new(-1, 4), new(-3, 3),
                new(-3, 6), new(-3, 5), new(-2, 4), new(0, 5), new(-1, 6),
                new(-2, 7), new(0, 5), new(-3, 6), new(-6, 7), new(-12, 8),
                new(-10, 4), new(-2, 6), new(0, 8), new(4, 9), new(5, 10),
                new(1, 5), new(0, 3), new(-2, 1), new(-5, 5), new(-6, 3),
                new(-1, 1), new(0, 2), new(0, 3), new(0, 4), new(0, 3),
                new(-2, 6), new(-6, 8), new(-10, 13), new(-9, 11), new(-5, 12),
                new(-1, 7), new(1, 5), new(1, 3), new(2, 0), new(-3, 1),
                new(-6, 3), new(-4, 5), new(-2, 4), new(-3, 6), new(-3, 6),
                new(-1, 5)
            };

            var thompsonPattern = new List<RecoilPoint>
            {
                new(0, 1), new(0, 2), new(0, 4), new(-1, 5), new(0, 3),
                new(0, 4), new(-2, 5), new(0, 3), new(0, 3), new(0, 1),
                new(0, 2), new(0, 4), new(0, 2), new(-1, 1), new(0, 2),
                new(0, 3), new(0, 1), new(0, 3), new(0, 3), new(0, 2),
                new(0, 1), new(0, 2), new(0, 3), new(0, 3), new(0, 3),
                new(0, 3), new(0, 3), new(-2, 4), new(0, 2), new(0, 3),
                new(0, 3), new(0, 1), new(0, 5), new(0, 3), new(-1, 3),
                new(0, 3), new(0, 3), new(0, 3), new(0, 3), new(0, 3),
                new(-2, 1), new(0, 2), new(0, 3), new(0, 3), new(0, 1),
                new(-1, 2), new(0, 1), new(0, 2), new(0, 1), new(0, 3),
                new(0, 3), new(-2, 3), new(0, 5), new(-1, 4), new(0, 5),
                new(-2, 3), new(0, 1), new(0, 3), new(-1, 3), new(0, 3),
                new(-2, 2), new(0, 4), new(0, 3), new(-1, 3), new(-2, 3),
                new(0, 2), new(0, 3), new(0, 1), new(0, 5), new(-1, 3),
                new(0, 3), new(-2, 4), new(0, 3), new(-1, 5), new(-2, 4),
                new(-1, 3), new(-2, 2), new(-1, 4), new(0, 3), new(-2, 5),
                new(0, 4), new(-1, 5), new(-2, 3), new(0, 3), new(0, 3),
                new(-1, 1), new(0, 2), new(-2, 4), new(-1, 6), new(-2, 8),
                new(-3, 10), new(-1, 9), new(-2, 8), new(-1, 7), new(0, 5),
                new(-2, 4), new(0, 3), new(-1, 3), new(0, 6), new(-2, 3),
                new(-1, 5), new(0, 4), new(-2, 5), new(-1, 4), new(0, 3),
                new(0, 3), new(-2, 3), new(-1, 3), new(0, 3), new(0, 2),
                new(-2, 3), new(0, 3), new(-1, 3), new(0, 3), new(-2, 3),
                new(0, 3), new(-1, 3), new(0, 3), new(-2, 3), new(-1, 3),
                new(0, 3), new(0, 1), new(0, 3), new(-2, 2), new(0, 4),
                new(0, 2), new(-1, 3), new(0, 3), new(0, 1), new(-2, 3),
                new(0, 2), new(0, 3), new(0, 1), new(-1, 3), new(0, 3),
                new(0, 3), new(-2, 3), new(0, 3), new(0, 3), new(0, 3),
                new(0, 3), new(-1, 5), new(0, 4), new(0, 5), new(-2, 4),
                new(0, 3), new(0, 3), new(0, 2), new(-1, 1), new(0, 3),
                new(0, 3), new(0, 3), new(0, 3), new(-2, 5), new(0, 4),
                new(-1, 8), new(-2, 7), new(-1, 8), new(-2, 9), new(-3, 9),
                new(-1, 9), new(-3, 10), new(-3, 12), new(-3, 14), new(-3, 13),
                new(-2, 9), new(-1, 5), new(0, 1), new(0, 3), new(-2, 6),
                new(-1, 9), new(-2, 11), new(-3, 10), new(-1, 11), new(0, 7),
                new(-2, 6), new(0, 5), new(-1, 3), new(0, 1), new(0, 3),
                new(-2, 5), new(0, 4), new(0, 6), new(-1, 6), new(-2, 6),
                new(-1, 8), new(-2, 7), new(-1, 6), new(0, 8), new(-3, 6),
                new(0, 6), new(-3, 7), new(-2, 6), new(-1, 5), new(0, 4),
                new(0, 3), new(-2, 3), new(-1, 3), new(0, 3), new(0, 3),
                new(-2, 3), new(0, 3)
            };

            var sarPattern = new List<RecoilPoint>
            {
                new(0,14), new(0,2), new(2,0), new(0,1), new(0,2),
                new(0,3), new(0,3), new(0,4), new(0,6), new(0,5),
                new(0,4), new(0,5), new(0,4), new(0,3), new(0,5),
                new(0,3), new(0,3), new(0,1), new(0,3), new(-1,3),
                new(0,2), new(0,1), new(0,3), new(-2,2), new(0,1),
                new(0,3), new(0,3), new(0,5), new(-1,4), new(0,3),
                new(0,3), new(-2,5), new(-1,4), new(0,2), new(0,4),
                new(0,2), new(-2,4), new(0,3), new(0,5), new(0,1),
                new(0,3), new(0,3), new(0,3), new(0,3), new(0,3),
                new(0,5), new(0,4), new(-1,3), new(0,5), new(0,4),
                new(-2,5), new(-1,7), new(0,6), new(0,5), new(-2,4),
                new(0,5), new(0,4), new(0,2), new(0,3), new(0,1),
                new(0,2), new(0,3), new(0,4), new(-1,3), new(0,5),
                new(0,6), new(0,7), new(0,8), new(0,6), new(0,7),
                new(0,6), new(0,5), new(0,6), new(0,6), new(0,7),
                new(0,6), new(0,5), new(0,7), new(0,6), new(0,6),
                new(0,3), new(0,8), new(0,4), new(0,3), new(0,5),
                new(0,4), new(-2,3), new(0,5), new(0,6), new(-1,6),
                new(0,6), new(0,6), new(-2,4), new(0,5), new(0,4),
                new(0,5), new(0,4), new(0,3), new(0,5), new(0,3),
                new(0,4), new(0,5), new(0,3), new(0,3), new(0,3),
                new(0,4), new(0,3), new(0,3), new(0,5), new(0,7),
                new(1,6), new(0,6), new(0,8), new(0,6), new(0,9),
                new(0,7), new(0,6), new(0,6), new(0,5), new(2,4),
                new(0,5), new(0,3), new(0,3), new(0,4), new(0,6),
                new(0,8), new(0,10), new(1,12), new(3,11), new(2,7),
                new(1,8), new(2,7), new(0,6), new(0,6), new(0,9),
                new(0,6), new(0,6), new(1,6), new(0,2), new(0,1),
            };

            var pythonPattern = new List<RecoilPoint>
            {
                new(2, 0), new(0, 13), new(-3, 24), new(-1, 26), new(-2, 19),
                new(0, 8), new(0, 1), new(0, -1), new(0, 9), new(0, 24),
                new(0, 24), new(1, 25), new(2, 17), new(1, 9), new(0, 4),
                new(2, 0), new(0, 6), new(-1, 29), new(0, 43), new(0, 33),
                new(-2, 30), new(-1, 18), new(0, 5), new(0, 1), new(1, 18),
                new(0, 39), new(0, 45), new(-1, 33), new(0, 26), new(-2, 25),
                new(-1, 24), new(0, 20), new(0, 13), new(-2, 3), new(-1, 18),
                new(-2, 38), new(-3, 51), new(0, 63), new(3, 57), new(0, 51),
                new(-1, 33), new(0, 16), new(-2, 8), new(-1, 0), new(0, -1)
            };

            _weaponProfiles["AK-47"] = new WeaponProfile
            {
                Name = "AK-47",
                Pattern = ak47Pattern,
                ControlX = 50,
                ControlY = 50,
                IsHipfire = false,
                Sensitivity = 1.0f,
                AdsSensitivity = 0.12f,
                FireRate = 9
            };
            _weaponProfiles["HMLG"] = new WeaponProfile
            {
                Name = "HMLG",
                Pattern = HMLGPattern,
                ControlX = 50,
                ControlY = 50,
                IsHipfire = false,
                Sensitivity = 1.0f,
                AdsSensitivity = 0.12f,
                FireRate = 9
            };
            _weaponProfiles["M249"] = new WeaponProfile
            {
                Name = "M249",
                Pattern = m249Pattern,
                ControlX = 50,
                ControlY = 50,
                IsHipfire = false,
                Sensitivity = 1.0f,
                AdsSensitivity = 0.12f,
                FireRate = 20
            };
            _weaponProfiles["MP5"] = new WeaponProfile
            {
                Name = "MP5",
                Pattern = thompsonPattern,
                ControlX = 50,
                ControlY = 50,
                IsHipfire = false,
                Sensitivity = 1.0f,
                AdsSensitivity = 0.12f,
                FireRate = 20
            };
            _weaponProfiles["Thompson"] = new WeaponProfile
            {
                Name = "Thompson",
                Pattern = thompsonPattern,
                ControlX = 50,
                ControlY = 50,
                IsHipfire = false,
                Sensitivity = 1.0f,
                AdsSensitivity = 0.12f,
                FireRate = 9
            };
            _weaponProfiles["SAR"] = new WeaponProfile
            {
                Name = "SAR",
                Pattern = sarPattern,
                ControlX = 50,
                ControlY = 50,
                IsHipfire = false,
                Sensitivity = 1.0f,
                AdsSensitivity = 0.12f,
                FireRate = 40
            };
            _weaponProfiles["SAP"] = new WeaponProfile
            {
                Name = "SAP",
                Pattern = sarPattern,
                ControlX = 50,
                ControlY = 50,
                IsHipfire = false,
                Sensitivity = 1.0f,
                AdsSensitivity = 0.12f,
                FireRate = 40
            };
            _weaponProfiles["SKS"] = new WeaponProfile
            {
                Name = "SKS",
                Pattern = sarPattern,
                ControlX = 50,
                ControlY = 50,
                IsHipfire = false,
                Sensitivity = 1.0f,
                AdsSensitivity = 0.12f,
                FireRate = 40
            };
            _weaponProfiles["M39"] = new WeaponProfile
            {
                Name = "M39",
                Pattern = sarPattern,
                ControlX = 50,
                ControlY = 50,
                IsHipfire = false,
                Sensitivity = 1.0f,
                AdsSensitivity = 0.12f,
                FireRate = 40
            };
            _weaponProfiles["PROTOTYPE-17"] = new WeaponProfile
            {
                Name = "ROTOTYPE-17",
                Pattern = sarPattern,
                ControlX = 50,
                ControlY = 50,
                IsHipfire = false,
                Sensitivity = 1.0f,
                AdsSensitivity = 0.12f,
                FireRate = 40
            };
            _weaponProfiles["SMG"] = new WeaponProfile
            {
                Name = "SMG",
                Pattern = thompsonPattern,
                ControlX = 50,
                ControlY = 50,
                IsHipfire = false,
                Sensitivity = 1.0f,
                AdsSensitivity = 0.12f,
                FireRate = 40
            };
            _weaponProfiles["Python"] = new WeaponProfile
            {
                Name = "Python",
                Pattern = pythonPattern,
                ControlX = 50,
                ControlY = 50,
                IsHipfire = false,
                Sensitivity = 1.0f,
                AdsSensitivity = 0.12f,
                FireRate = 40
            };
            _weaponProfiles["High Revolver"] = new WeaponProfile
            {
                Name = "High Revolver",
                Pattern = pythonPattern,
                ControlX = 50,
                ControlY = 50,
                IsHipfire = false,
                Sensitivity = 1.0f,
                AdsSensitivity = 0.12f,
                FireRate = 40
            };
            _weaponProfiles["Revolver"] = new WeaponProfile
            {
                Name = "Revolver",
                Pattern = sarPattern,
                ControlX = 50,
                ControlY = 50,
                IsHipfire = false,
                Sensitivity = 1.0f,
                AdsSensitivity = 0.12f,
                FireRate = 40
            };
            _weaponProfiles["LR-300"] = new WeaponProfile
            {
                Name = "LR-300",
                Pattern = sarPattern,
                ControlX = 50,
                ControlY = 50,
                IsHipfire = false,
                Sensitivity = 1.0f,
                AdsSensitivity = 0.12f,
                FireRate = 40
            };
            _weaponProfiles["Handmade SMG"] = new WeaponProfile
            {
                Name = "Handmade SMG",
                Pattern = sarPattern,
                ControlX = 50,
                ControlY = 50,
                IsHipfire = false,
                Sensitivity = 1.0f,
                AdsSensitivity = 0.12f,
                FireRate = 40
            };
        }

        private void Hook_KeyPressed(object sender, KeyboardHookEventArgs e)
        {
            if (e.Data.KeyCode == KeyCode.VcInsert)
            {
                Dispatcher.Invoke(() =>
                {
                    _isWindowVisible = !_isWindowVisible;
                    Visibility = _isWindowVisible ? Visibility.Visible : Visibility.Hidden;
                });
            }
            else if (e.Data.KeyCode == KeyCode.VcLeftControl)
            {
                _isCrouching = true;
            }
        }

        private void Hook_KeyReleased(object sender, KeyboardHookEventArgs e)
        {
            if (e.Data.KeyCode == KeyCode.VcLeftControl)
            {
                _isCrouching = false;
            }
        }

        private void Hook_MousePressed(object sender, MouseHookEventArgs e)
        {
            if (e.Data.Button == HookMouseButton.Button1)   // LM
            {
                _isShooting = true;
                CheckAndStartRecoilCompensation();
            }
            else if (e.Data.Button == HookMouseButton.Button2) // RM
            {
                _isAiming = true;
                CheckAndStartRecoilCompensation();
            }
        }

        private void Hook_MouseReleased(object sender, MouseHookEventArgs e)
        {
            if (e.Data.Button == HookMouseButton.Button1)   // LM
            {
                _isShooting = false;
                CheckAndStopRecoilCompensation();
            }
            else if (e.Data.Button == HookMouseButton.Button2) // RM
            {
                _isAiming = false;
                CheckAndStopRecoilCompensation();
            }
        }

        private void CheckAndStartRecoilCompensation()
        {
            if (_currentWeapon == null || !_isEnabled) return;

            bool shouldActivate = _currentWeapon.IsHipfire ?
                _isShooting :
                (_isShooting && _isAiming);

            if (shouldActivate)
            {
                StartRecoilCompensation();
            }
        }

        private void CheckAndStopRecoilCompensation()
        {
            if (_currentWeapon == null || !_isEnabled) return;

            bool shouldStop = _currentWeapon.IsHipfire ?
                !_isShooting :
                (!_isShooting || !_isAiming);

            if (shouldStop)
            {
                StopRecoilCompensation();
            }
        }

        private async void StartRecoilCompensation()
        {
            if (!_isEnabled || _currentWeapon == null) return;

            _recoilCancellation?.Cancel();
            _recoilCancellation = new CancellationTokenSource();
            _currentPatternIndex = 0;

            await Task.Run(() => ApplyRecoilCompensation(_recoilCancellation.Token));
        }

        private void StopRecoilCompensation()
        {
            _recoilCancellation?.Cancel();
        }

        private void ApplyRecoilCompensation(CancellationToken cancellationToken)
        {
            if (_currentWeapon?.Pattern == null || _currentWeapon.Pattern.Count == 0)
                return;

            const float defaultRecoilMultiplier = 1.55f;

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    bool shouldCompensate = _currentWeapon.IsHipfire ?
                        _isShooting :
                        (_isShooting && _isAiming);

                    if (!shouldCompensate)
                        break;

                    string selectedSight = "";
                    string selectedMuzzle = "";

                    Dispatcher.Invoke(() =>
                    {
                        selectedSight = SightComboBox.SelectedItem as string;
                        selectedMuzzle = MuzzleComboBox.SelectedItem as string;
                    });

                    float sightModifier = _sightModifiers.ContainsKey(selectedSight) ?
                        _sightModifiers[selectedSight] : 1.0f;

                    float muzzleModifier = _muzzleModifiers.ContainsKey(selectedMuzzle) ?
                        _muzzleModifiers[selectedMuzzle] : 1.0f;

                    float controlX = _currentWeapon.ControlX / 100f;
                    float controlY = _currentWeapon.ControlY / 100f;

                    float currentSensitivity = _isAiming ?
                        _currentWeapon.AdsSensitivity :
                        _currentWeapon.Sensitivity;

                    if (_isCrouching)
                    {
                        controlX *= 0.5f;
                        controlY *= 0.5f;
                    }

                    controlX *= sightModifier * muzzleModifier;
                    controlY *= sightModifier * muzzleModifier;

                    var point = _currentWeapon.Pattern[_currentPatternIndex];

                    float sensitivityFactor = 1.0f / currentSensitivity;

                    int moveX = (int)(point.X * controlX * sensitivityFactor * defaultRecoilMultiplier);
                    int moveY = (int)(point.Y * controlY * sensitivityFactor * defaultRecoilMultiplier);

                    if (moveX != 0 || moveY != 0)
                    {
                        NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_MOVE, moveX, moveY, 0, UIntPtr.Zero);
                    }

                    _currentPatternIndex = (_currentPatternIndex + 1) % _currentWeapon.Pattern.Count;

                    Thread.Sleep(_currentWeapon.FireRate);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"play pattern error: {ex.Message}");
                    break;
                }
            }
        }


        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
                this.DragMove();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = WindowState.Minimized;
        }

        private void ProcessMonitor_ProcessStateChanged(object sender, bool isRunning)
        {
            Dispatcher.Invoke(() =>
            {
                StatusText.Text = isRunning ?
                    "Status: Rust detected - Scripts Init sucessfully!" :
                    "Status: Waiting for Rust...";
                _isEnabled = isRunning && (EnableCheckBox.IsChecked == true);
            });
        }

        private void WeaponComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (WeaponComboBox.SelectedItem is string weaponName && _weaponProfiles.ContainsKey(weaponName))
            {
                _currentWeapon = _weaponProfiles[weaponName];
                ControlXSlider.Value = _currentWeapon.ControlX;
                ControlYSlider.Value = _currentWeapon.ControlY;
                HipfireCheckBox.IsChecked = _currentWeapon.IsHipfire;
                SensitivitySlider.Value = _currentWeapon.Sensitivity;
                AdsSensitivitySlider.Value = _currentWeapon.AdsSensitivity;
            }
        }

        private void ControlSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_currentWeapon != null)
            {
                _currentWeapon.ControlX = (int)ControlXSlider.Value;
                _currentWeapon.ControlY = (int)ControlYSlider.Value;
            }
        }

        private void EnableCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            _isEnabled = EnableCheckBox.IsChecked == true && _processMonitor.IsRunning;
        }

        private void EnableCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            _isEnabled = false;
        }

        private void HipfireCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (_currentWeapon != null)
            {
                _currentWeapon.IsHipfire = HipfireCheckBox.IsChecked == true;
            }
        }

        private void SensitivitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_currentWeapon != null)
            {
                _currentWeapon.Sensitivity = (float)SensitivitySlider.Value;
            }
        }

        private void AdsSensitivitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_currentWeapon != null)
            {
                _currentWeapon.AdsSensitivity = (float)AdsSensitivitySlider.Value;
            }
        }

        private void SightComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SightComboBox.SelectedItem is string selectedSight && _sightModifiers.ContainsKey(selectedSight))
            {
                float modifier = (_sightModifiers[selectedSight] - 1.0f) * 100;
                SightModifierText.Text = $"{(modifier >= 0 ? "+" : "")}{modifier:F0}%";
            }
        }

        private void MuzzleComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (MuzzleComboBox.SelectedItem is string selectedMuzzle && _muzzleModifiers.ContainsKey(selectedMuzzle))
            {
                float modifier = (_muzzleModifiers[selectedMuzzle] - 1.0f) * 100;
                MuzzleModifierText.Text = $"{(modifier >= 0 ? "+" : "")}{modifier:F0}%";
            }
        }
    }

    public class WeaponProfile
    {
        public string Name { get; set; }
        public List<RecoilPoint> Pattern { get; set; }
        public int ControlX { get; set; }
        public int ControlY { get; set; }
        public bool IsHipfire { get; set; }
        public float Sensitivity { get; set; }
        public float AdsSensitivity { get; set; }
        public int FireRate { get; set; } = 100;
    }

    public class RecoilPoint
    {
        public int X { get; set; }
        public int Y { get; set; }

        public RecoilPoint(int x, int y)
        {
            X = x;
            Y = y;
        }
    }

    public class ProcessMonitor
    {
        public event EventHandler<bool> ProcessStateChanged;
        public bool IsRunning { get; private set; }

        private readonly System.Timers.Timer _timer;
        private bool _lastState;

        public ProcessMonitor()
        {
            _timer = new System.Timers.Timer(1000);
            _timer.Elapsed += CheckProcess;
        }

        public void StartMonitoring(string processName)
        {
            _timer.Start();
        }

        public void StopMonitoring()
        {
            _timer.Stop();
        }

        private void CheckProcess(object sender, System.Timers.ElapsedEventArgs e)
        {
            bool isRunning = Process.GetProcesses()
                .Any(p => p.ProcessName.Contains("RustClient", StringComparison.OrdinalIgnoreCase) ||
                         p.ProcessName.Contains("rust", StringComparison.OrdinalIgnoreCase));

            if (isRunning != _lastState)
            {
                _lastState = isRunning;
                IsRunning = isRunning;
                ProcessStateChanged?.Invoke(this, isRunning);
            }
        }
    }

    public static class NativeMethods
    {
        public const uint MOUSEEVENTF_MOVE = 0x0001;

        [DllImport("user32.dll")]
        public static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, UIntPtr dwExtraInfo);
    }
}