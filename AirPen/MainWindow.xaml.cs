using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace AirPen
{
    public partial class MainWindow : Window
    {
        private readonly string folderPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Signatures"));
        private readonly Duration viewAnimationDuration = new Duration(TimeSpan.FromMilliseconds(220));

        private readonly DispatcherTimer samplingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        private readonly Stopwatch samplingStopwatch = new Stopwatch();
        private readonly List<List<MotionPoint>> tremorStepSamples = new List<List<MotionPoint>>();
        private readonly List<List<MotionPoint>> gestureTrialSamples = new List<List<MotionPoint>>();
        private readonly Random random = new Random();
        private readonly string[] gestureNames = { "ósemka", "fala", "litera M", "podwójna pętla", "zygzak gwiazdowy", "haczyk" };

        private InkCanvas? activeSamplingCanvas;
        private List<MotionPoint> activeMotionSamples = new List<MotionPoint>();
        private int tremorStepIndex;
        private int currentGesturePatternIndex;

        private Color currentPenColor = (Color)ColorConverter.ConvertFromString("#18212F");
        private double currentPenSize = 3;

        public MainWindow()
        {
            InitializeComponent();

            samplingTimer.Tick += SamplingTimer_Tick;
            TremorGuideCanvas.SizeChanged += (s, e) => DrawTremorGuide();
            GestureGuideCanvas.SizeChanged += (s, e) => DrawGestureGuide();

            ConfigureTestCanvas(TremorCanvas, Colors.Black);
            ConfigureTestCanvas(GestureCanvas, Colors.Black);
            UpdateSelectedColorButton(ColorBlackButton);
            UpdateSelectedPenSizeButton(PenSizeSmallButton);

            UpdateDrawingAttributes();
        }

        // ==========================================
        // NAWIGACJA OKIEN
        // ==========================================
        private void ShowWriteGrid_Click(object sender, RoutedEventArgs e)
        {
            ResetWriteButtons();
            ShowView(WriteGrid);
            SignatureCanvas.Strokes.Clear();
            UpdateCanvasHint();
        }

        private void ShowBrowseGrid_Click(object sender, RoutedEventArgs e)
        {
            LoadSavedSignatures();
            ShowView(BrowseGrid);
        }

        private void ShowTremorTestGrid_Click(object sender, RoutedEventArgs e)
        {
            ResetTremorTest();
            ShowView(TremorTestGrid);
            DrawTremorGuide();
        }

        private void ShowGestureTestGrid_Click(object sender, RoutedEventArgs e)
        {
            ResetGestureTest();
            ShowView(GestureTestGrid);
            DrawGestureGuide();
        }

        private void BackToMenu_Click(object? sender, RoutedEventArgs? e)
        {
            ShowView(MenuGrid);
        }

        private void BackToBrowse_Click(object sender, RoutedEventArgs e)
        {
            ShowView(BrowseGrid);
        }

        // ==========================================
        // NOWA LOGIKA POTWIERDZENIA ZAPISU (BEZ POPUPU)
        // ==========================================
        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (SignatureCanvas.Strokes.Count == 0)
            {
                MessageBox.Show("Obszar podpisu jest pusty!", "Uwaga", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Podmiana przycisków w prawym dolnym rogu na Potwierdź / Cofnij
            WriteActionPanel.Visibility = Visibility.Collapsed;
            WriteConfirmPanel.Visibility = Visibility.Visible;
        }

        private void CancelSave_Click(object sender, RoutedEventArgs e)
        {
            // Powrót do standardowych przycisków akcji
            ResetWriteButtons();
        }

        private void ConfirmSave_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!Directory.Exists(folderPath))
                {
                    Directory.CreateDirectory(folderPath);
                }

                string fileName = $"Podpis_{DateTime.Now:yyyyMMdd_HHmmss}.png";
                string fullPath = System.IO.Path.Combine(folderPath, fileName);

                Rect bounds = SignatureCanvas.Strokes.GetBounds();
                bounds.Inflate(25, 25);

                DrawingVisual visual = new DrawingVisual();
                using (DrawingContext dc = visual.RenderOpen())
                {
                    dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, bounds.Width, bounds.Height));
                    dc.PushTransform(new TranslateTransform(-bounds.X, -bounds.Y));
                    SignatureCanvas.Strokes.Draw(dc);
                    dc.Pop();
                }

                RenderTargetBitmap renderBitmap = new RenderTargetBitmap((int)bounds.Width, (int)bounds.Height, 96d, 96d, PixelFormats.Default);
                renderBitmap.Render(visual);

                PngBitmapEncoder encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(renderBitmap));

                using (FileStream fileStream = new FileStream(fullPath, FileMode.Create))
                {
                    encoder.Save(fileStream);
                }

                ResetWriteButtons();
                BackToMenu_Click(null, null);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Błąd zapisu: {ex.Message}", "Błąd", MessageBoxButton.OK, MessageBoxImage.Error);
                ResetWriteButtons();
            }
        }

        private void ResetWriteButtons()
        {
            WriteConfirmPanel.Visibility = Visibility.Collapsed;
            WriteActionPanel.Visibility = Visibility.Visible;
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            SignatureCanvas.Strokes.Clear();
            ResetWriteButtons();
            UpdateCanvasHint();
        }

        // ==========================================
        // OBSŁUGA INTERFEJSU PISANIA
        // ==========================================
        private void ColorButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not string colorValue) return;

            currentPenColor = (Color)ColorConverter.ConvertFromString(colorValue);
            UpdateDrawingAttributes();
            UpdateSelectedColorButton(button);
        }

        private void PenSizeButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not string sizeValue || !double.TryParse(sizeValue, out double penSize)) return;

            currentPenSize = penSize;
            UpdateDrawingAttributes();
            UpdateSelectedPenSizeButton(button);
        }

        private void SignatureCanvas_StrokeCollected(object sender, InkCanvasStrokeCollectedEventArgs e)
        {
            UpdateCanvasHint();
        }

        private void TremorCanvas_StrokeCollected(object sender, InkCanvasStrokeCollectedEventArgs e)
        {
            TremorCanvasHint.Visibility = TremorCanvas.Strokes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void GestureCanvas_StrokeCollected(object sender, InkCanvasStrokeCollectedEventArgs e)
        {
            GestureCanvasHint.Visibility = GestureCanvas.Strokes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateDrawingAttributes()
        {
            SignatureCanvas.DefaultDrawingAttributes = new System.Windows.Ink.DrawingAttributes
            {
                Color = currentPenColor,
                Width = currentPenSize,
                Height = currentPenSize,
                FitToCurve = true,
                StylusTip = System.Windows.Ink.StylusTip.Ellipse
            };
        }

        private void UpdateSelectedColorButton(Button selectedButton)
        {
            Button[] buttons = { ColorBlackButton, ColorBlueButton, ColorTealButton, ColorRoseButton };

            foreach (Button button in buttons)
            {
                button.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#CBD5E1"));
                button.BorderThickness = new Thickness(2);
            }

            selectedButton.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2563EB"));
            selectedButton.BorderThickness = new Thickness(3);
        }

        private void UpdateSelectedPenSizeButton(Button selectedButton)
        {
            Button[] buttons = { PenSizeThinButton, PenSizeSmallButton, PenSizeMediumButton, PenSizeLargeButton, PenSizeHugeButton };

            foreach (Button button in buttons)
            {
                button.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#CBD5E1"));
                button.BorderThickness = new Thickness(2);
            }

            selectedButton.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2563EB"));
            selectedButton.BorderThickness = new Thickness(3);
        }

        private void UpdateCanvasHint()
        {
            CanvasHint.Visibility = SignatureCanvas.Strokes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // ==========================================
        // TESTY BIOMETRYCZNE - PRÓBKOWANIE RUCHU
        // ==========================================
        private void SamplingCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not InkCanvas canvas) return;
            activeSamplingCanvas = canvas;
            activeMotionSamples = new List<MotionPoint>();
            samplingStopwatch.Restart();
            RecordSample();
            samplingTimer.Start();
        }

        private void SamplingCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            StopSampling();
        }

        private void SamplingCanvas_MouseLeave(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            StopSampling();
        }

        private void SamplingTimer_Tick(object? sender, EventArgs e)
        {
            RecordSample();
        }

        private void RecordSample()
        {
            if (activeSamplingCanvas is null) return;
            Point position = Mouse.GetPosition(activeSamplingCanvas);

            if (position.X < 0 || position.Y < 0 || position.X > activeSamplingCanvas.ActualWidth || position.Y > activeSamplingCanvas.ActualHeight) return;

            activeMotionSamples.Add(new MotionPoint(position, samplingStopwatch.Elapsed.TotalMilliseconds));
        }

        private void StopSampling()
        {
            if (!samplingTimer.IsEnabled) return;

            RecordSample();
            samplingTimer.Stop();
            samplingStopwatch.Stop();
            activeSamplingCanvas = null;
        }

        private void ConfigureTestCanvas(InkCanvas canvas, Color color)
        {
            canvas.DefaultDrawingAttributes = new System.Windows.Ink.DrawingAttributes
            {
                Color = color,
                Width = 3,
                Height = 3,
                FitToCurve = false,
                StylusTip = System.Windows.Ink.StylusTip.Ellipse
            };
        }

        // ==========================================
        // LOGIKA PRZEGLĄDARKI
        // ==========================================
        private void LoadSavedSignatures()
        {
            SignaturesPanel.Children.Clear();

            if (!Directory.Exists(folderPath))
            {
                AddEmptyState("Folder z podpisami jeszcze nie istnieje.");
                return;
            }

            string[] files = Directory.GetFiles(folderPath, "*.png");

            if (files.Length == 0)
            {
                AddEmptyState("Brak zapisanych podpisów.");
                return;
            }

            foreach (string file in files)
            {
                BitmapImage bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(file);
                bitmap.EndInit();

                Image img = new Image
                {
                    Source = bitmap,
                    Stretch = Stretch.Uniform,
                    Margin = new Thickness(10)
                };

                Border border = new Border
                {
                    BorderBrush = (Brush)FindResource("LineBrush"),
                    BorderThickness = new Thickness(1),
                    Background = Brushes.White,
                    CornerRadius = new CornerRadius(10),
                    Child = img,
                    Margin = new Thickness(7),
                    Cursor = Cursors.Hand,
                    Effect = (System.Windows.Media.Effects.Effect)FindResource("SoftShadow"),
                    RenderTransform = new ScaleTransform(1, 1),
                    RenderTransformOrigin = new Point(0.5, 0.5)
                };

                border.MouseEnter += Thumbnail_MouseEnter;
                border.MouseLeave += Thumbnail_MouseLeave;
                border.MouseLeftButtonDown += (s, e) => ShowSignatureDetail(file);

                SignaturesPanel.Children.Add(border);
            }
        }

        private void ShowSignatureDetail(string filePath)
        {
            BitmapImage bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(filePath);
            bitmap.EndInit();

            DetailImage.Source = bitmap;
            ShowView(DetailGrid);
        }

        private void AddEmptyState(string text)
        {
            TextBlock emptyTxt = new TextBlock
            {
                Text = text,
                Margin = new Thickness(10),
                FontStyle = FontStyles.Italic,
                Foreground = (Brush)FindResource("MutedTextBrush")
            };
            SignaturesPanel.Children.Add(emptyTxt);
        }

        private void Thumbnail_MouseEnter(object sender, MouseEventArgs e) => AnimateThumbnail(sender, 1.035);
        private void Thumbnail_MouseLeave(object sender, MouseEventArgs e) => AnimateThumbnail(sender, 1);

        private static void AnimateThumbnail(object sender, double scale)
        {
            if (sender is not Border border || border.RenderTransform is not ScaleTransform transform) return;

            // Płynna animacja najechania myszką na podpis w bazie
            Duration duration = new Duration(TimeSpan.FromMilliseconds(140));
            DoubleAnimation animation = new DoubleAnimation(scale, duration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            transform.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
            transform.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
        }

        // ==========================================
        // TEST DRŻENIA (Z MODALNYM POPUPEM WYNIKÓW)
        // ==========================================
        private void ClearTremorStep_Click(object sender, RoutedEventArgs e)
        {
            TremorCanvas.Strokes.Clear();
            activeMotionSamples.Clear();
            TremorCanvasHint.Visibility = Visibility.Visible;
        }

        private void TremorNextButton_Click(object sender, RoutedEventArgs e)
        {
            if (tremorStepSamples.Count >= 5)
            {
                ResetTremorTest();
                return;
            }

            if (TremorCanvas.Strokes.Count == 0 || activeMotionSamples.Count < 8)
            {
                MessageBox.Show("Najpierw narysuj aktualny szlaczek.", "Brak próbki", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            tremorStepSamples.Add(new List<MotionPoint>(activeMotionSamples));

            if (tremorStepSamples.Count >= 5)
            {
                AnalyzeTremorTest();
                TremorNextButton.Content = "Rozpocznij od nowa";
                return;
            }

            tremorStepIndex++;
            TremorCanvas.Strokes.Clear();
            activeMotionSamples.Clear();
            UpdateTremorStepText();
            DrawTremorGuide();
            TremorCanvasHint.Visibility = Visibility.Visible;
        }

        private void ResetTremorTest()
        {
            StopSampling();
            tremorStepSamples.Clear();
            activeMotionSamples.Clear();
            tremorStepIndex = 0;
            TremorCanvas.Strokes.Clear();
            TremorCanvasHint.Visibility = Visibility.Visible;
            TremorResultText.Text = "Wykonaj wszystkie 5 szlaczków, żeby dostać ocenę stabilności.";
            TremorNextButton.Content = "Zapisz i dalej";
            UpdateTremorStepText();
            DrawTremorGuide();
        }

        private void UpdateTremorStepText()
        {
            string[] names = { "prosta linia", "fala", "zygzak", "pętla", "spirala" };
            TremorStepText.Text = $"Szlaczek {tremorStepIndex + 1} z 5";
            TremorInstructionText.Text = $"Zadanie: {names[tremorStepIndex]}. Prowadź długopis spokojnie po jasnej linii. Aplikacja zapisuje ruch kursora co około 10 ms.";
        }

        // ==========================================
        // TEST GESTU (Z MODALNYM POPUPEM WYNIKÓW)
        // ==========================================
        private void ClearGestureTrial_Click(object sender, RoutedEventArgs e)
        {
            GestureCanvas.Strokes.Clear();
            activeMotionSamples.Clear();
            GestureCanvasHint.Visibility = Visibility.Visible;
        }

        private void GestureNextButton_Click(object sender, RoutedEventArgs e)
        {
            if (gestureTrialSamples.Count >= 3)
            {
                ResetGestureTest();
                return;
            }

            if (GestureCanvas.Strokes.Count == 0 || activeMotionSamples.Count < 8)
            {
                MessageBox.Show("Najpierw narysuj gest.", "Brak próbki", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            gestureTrialSamples.Add(new List<MotionPoint>(activeMotionSamples));

            if (gestureTrialSamples.Count >= 3)
            {
                AnalyzeGestureTest();
                GestureNextButton.Content = "Rozpocznij od nowa";
                return;
            }

            GestureCanvas.Strokes.Clear();
            activeMotionSamples.Clear();
            GestureTrialText.Text = $"Próba {gestureTrialSamples.Count + 1} z 3";
            GestureResultText.Text = $"Zapisano {gestureTrialSamples.Count}/3 prób. Powtórz ten sam znak możliwie podobnym ruchem.";
            GestureCanvasHint.Visibility = Visibility.Visible;
        }

        private void ResetGestureTest()
        {
            StopSampling();
            currentGesturePatternIndex = random.Next(gestureNames.Length);
            gestureTrialSamples.Clear();
            activeMotionSamples.Clear();
            GestureCanvas.Strokes.Clear();
            GestureCanvasHint.Visibility = Visibility.Visible;
            GestureTrialText.Text = "Próba 1 z 3";
            GestureInstructionText.Text = $"Wylosowany znak: {gestureNames[currentGesturePatternIndex]}. Narysuj go 3 razy możliwie podobnym ruchem.";
            GestureResultText.Text = "Wykonaj 3 powtórzenia wylosowanego gestu, żeby dostać ocenę biometrycznej powtarzalności.";
            GestureNextButton.Content = "Zapisz próbę";
            DrawGestureGuide();
        }

        // ==========================================
        // ALGORYTMY BIOMETRYCZNE Z NOWYMI POPUPAMI
        // ==========================================
        private void ClosePopup_Click(object sender, RoutedEventArgs e)
        {
            ResultPopupOverlay.Visibility = Visibility.Collapsed;
        }

        private void AnalyzeTremorTest()
        {
            List<MotionMetrics> metrics = tremorStepSamples.Select(CalculateMetrics).ToList();
            double averageJitter = metrics.Average(m => m.Jitter);
            double averageSpeedInstability = metrics.Average(m => m.SpeedInstability);
            double averageTurns = metrics.Average(m => m.DirectionNoise);

            double penalty = Math.Clamp(averageJitter * 5.0 + averageSpeedInstability * 50.0 + averageTurns * 20.0, 0, 100);
            double stabilityScore = 100 - penalty;

            string level = stabilityScore >= 80 ? "bardzo wysoka" : stabilityScore >= 45 ? "przeciętna" : "niska";
            string conclusion = stabilityScore >= 80
                ? "Ruch jest pewny, płynny i celowy. Doskonała stabilność."
                : stabilityScore >= 45
                    ? "Zauważono lekkie wahania na zakrętach lub zmiany tempa."
                    : "Wykryto bardzo silne mikrodrżenia i przypadkowe szarpnięcia. Ruch mocno zaburzony.";

            string formattedText = $"Wskaźnik stabilności dłoni: {stabilityScore:0}/100 ({level}).\n\n" +
                                   $"• Mikrofalowanie: {averageJitter:0.0} px\n" +
                                   $"• Niestabilność prędkości: {averageSpeedInstability:P0}\n" +
                                   $"• Szarpnięcia toru: {averageTurns:0.0}\n\n" +
                                   $"{conclusion}";

            // Aktualizacja panelu bocznego
            TremorResultText.Text = formattedText;

            // Wyświetlenie pięknego autorskiego Popupa modalnego
            PopupTitleText.Text = " Screening Drżenia Dłoni";
            PopupBodyText.Text = formattedText;
            ResultPopupOverlay.Visibility = Visibility.Visible;
        }

        private void AnalyzeGestureTest()
        {
            List<MotionMetrics> metrics = gestureTrialSamples.Select(CalculateMetrics).ToList();
            double lengthCv = CoefficientOfVariation(metrics.Select(m => m.PathLength));
            double durationCv = CoefficientOfVariation(metrics.Select(m => m.DurationMs));
            double speedCv = CoefficientOfVariation(metrics.Select(m => m.AverageSpeed));
            double jitterCv = CoefficientOfVariation(metrics.Select(m => m.Jitter));

            double mismatch = lengthCv * 0.35 + durationCv * 0.30 + speedCv * 0.35 + jitterCv * 0.20;
            double consistency = Math.Clamp(100 - (mismatch * 250), 0, 100);

            string level = consistency >= 85 ? "wysoka" : consistency >= 50 ? "średnia" : "bardzo niska";
            string conclusion = consistency == 0
                ? "To nie jest powtarzalny podpis, tylko losowe ruchy. Brak spójnych cech biometrycznych."
                : "Test ukończony pomyślnie. Profil dynamiczny zapisany w strukturze behawioralnej.";

            string formattedText = $"Spójność biometryczna gestu: {consistency:0}/100 ({level}).\n\n" +
                                   $"• Rozbieżność długości ścieżki: {lengthCv:P0}\n" +
                                   $"• Rozbieżność czasu wykonania: {durationCv:P0}\n" +
                                   $"• Odchył tempa pisania: {speedCv:P0}\n\n" +
                                   $"{conclusion}";

            // Aktualizacja panelu bocznego
            GestureResultText.Text = formattedText;

            // Wyświetlenie pięknego autorskiego Popupa modalnego
            PopupTitleText.Text = " Powtarzalność Dynamiczna";
            PopupBodyText.Text = formattedText;
            ResultPopupOverlay.Visibility = Visibility.Visible;
        }

        private void ShowView(UIElement targetView)
        {
            UIElement[] views = { MenuGrid, WriteGrid, TremorTestGrid, GestureTestGrid, BrowseGrid, DetailGrid };

            foreach (UIElement view in views)
            {
                if (view != targetView)
                {
                    view.Visibility = Visibility.Collapsed;
                    view.Opacity = 0;
                }
            }

            targetView.Visibility = Visibility.Visible;
            targetView.Opacity = 0;

            if (targetView.RenderTransform is TranslateTransform transform)
            {
                transform.Y = 14;
                transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, viewAnimationDuration)
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
            }

            targetView.BeginAnimation(OpacityProperty, new DoubleAnimation(1, viewAnimationDuration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        }

        private MotionMetrics CalculateMetrics(List<MotionPoint> samples)
        {
            if (samples.Count < 3) return new MotionMetrics(0, 0, 0, 0, 0, 0);

            double pathLength = 0;
            List<double> speeds = new List<double>();

            for (int i = 1; i < samples.Count; i++)
            {
                double distance = Distance(samples[i - 1].Position, samples[i].Position);
                double deltaTime = Math.Max(1, samples[i].TimeMs - samples[i - 1].TimeMs);
                pathLength += distance;
                speeds.Add(distance / deltaTime * 1000.0);
            }

            double duration = Math.Max(1, samples[^1].TimeMs - samples[0].TimeMs);
            double averageSpeed = pathLength / duration * 1000.0;
            double speedInstability = CoefficientOfVariation(speeds);
            double jitter = CalculateJitter(samples);
            double directionNoise = CalculateDirectionNoise(samples);

            return new MotionMetrics(pathLength, duration, averageSpeed, speedInstability, jitter, directionNoise);
        }

        private double CalculateJitter(List<MotionPoint> samples)
        {
            if (samples.Count < 5) return 0;
            double total = 0;
            int count = 0;

            for (int i = 1; i < samples.Count - 1; i++)
            {
                total += DistanceFromLine(samples[i].Position, samples[i - 1].Position, samples[i + 1].Position);
                count++;
            }
            return count == 0 ? 0 : total / count;
        }

        private double CalculateDirectionNoise(List<MotionPoint> samples)
        {
            if (samples.Count < 4) return 0;
            double total = 0;
            int count = 0;

            for (int i = 2; i < samples.Count; i++)
            {
                Vector a = samples[i - 1].Position - samples[i - 2].Position;
                Vector b = samples[i].Position - samples[i - 1].Position;
                if (a.Length < 0.1 || b.Length < 0.1) continue;
                double dot = Math.Clamp(Vector.Multiply(a, b) / (a.Length * b.Length), -1, 1);
                total += Math.Acos(dot);
                count++;
            }
            return count == 0 ? 0 : total / count;
        }

        private double DistanceFromLine(Point point, Point lineStart, Point lineEnd)
        {
            double dx = lineEnd.X - lineStart.X;
            double dy = lineEnd.Y - lineStart.Y;
            if (Math.Abs(dx) < 0.001 && Math.Abs(dy) < 0.001) return Distance(point, lineStart);
            return Math.Abs(dy * point.X - dx * point.Y + lineEnd.X * lineStart.Y - lineEnd.Y * lineStart.X) / Math.Sqrt(dx * dx + dy * dy);
        }

        private static double Distance(Point a, Point b)
        {
            double dx = a.X - b.X;
            double dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static double CoefficientOfVariation(IEnumerable<double> values)
        {
            List<double> cleanValues = values.Where(v => !double.IsNaN(v) && !double.IsInfinity(v) && v > 0).ToList();
            if (cleanValues.Count == 0) return 0;
            double average = cleanValues.Average();
            if (average <= 0.001) return 0;
            double variance = cleanValues.Average(v => Math.Pow(v - average, 2));
            return Math.Sqrt(variance) / average;
        }

        private void DrawTremorGuide()
        {
            if (TremorGuideCanvas.ActualWidth <= 0 || TremorGuideCanvas.ActualHeight <= 0) return;

            TremorGuideCanvas.Children.Clear();
            Polyline guide = CreateGuideLine();
            double width = TremorGuideCanvas.ActualWidth;
            double height = TremorGuideCanvas.ActualHeight;
            double midY = height / 2;

            switch (tremorStepIndex)
            {
                case 0:
                    guide.Points = new PointCollection { new Point(45, midY), new Point(width - 45, midY) };
                    break;
                case 1:
                    for (int i = 0; i <= 96; i++) guide.Points.Add(new Point(45 + (width - 90) * i / 96, midY + Math.Sin(i / 96.0 * Math.PI * 6) * 46));
                    break;
                case 2:
                    for (int i = 0; i <= 10; i++) guide.Points.Add(new Point(45 + (width - 90) * i / 10, i % 2 == 0 ? midY - 48 : midY + 48));
                    break;
                case 3:
                    for (int i = 0; i <= 120; i++) guide.Points.Add(new Point(width / 2 + Math.Sin(i / 120.0 * Math.PI * 2) * 150, midY + Math.Sin((i / 120.0 * Math.PI * 2) * 2) * 58));
                    break;
                default:
                    for (int i = 0; i <= 130; i++) guide.Points.Add(new Point(width / 2 + Math.Cos(i / 130.0 * Math.PI * 4.4) * (18 + i * 0.85), midY + Math.Sin(i / 130.0 * Math.PI * 4.4) * (18 + i * 0.85)));
                    break;
            }
            TremorGuideCanvas.Children.Add(guide);
        }

        private void DrawGestureGuide()
        {
            if (GestureGuideCanvas.ActualWidth <= 0 || GestureGuideCanvas.ActualHeight <= 0) return;

            GestureGuideCanvas.Children.Clear();
            Polyline guide = CreateGuideLine();
            double width = GestureGuideCanvas.ActualWidth;
            double height = GestureGuideCanvas.ActualHeight;
            double midY = GestureGuideCanvas.ActualHeight / 2;

            switch (currentGesturePatternIndex)
            {
                case 0:
                    for (int i = 0; i <= 140; i++) guide.Points.Add(new Point(width / 2 + Math.Sin(i / 140.0 * Math.PI * 2) * 170, midY + Math.Sin((i / 140.0 * Math.PI * 2) * 2) * 68));
                    break;
                case 1:
                    for (int i = 0; i <= 120; i++) guide.Points.Add(new Point(55 + (width - 110) * i / 120, midY + Math.Sin(i / 120.0 * Math.PI * 5) * 62));
                    break;
                case 2:
                    guide.Points = new PointCollection { new Point(70, midY + 70), new Point(120, midY - 75), new Point(width / 2, midY + 42), new Point(width - 120, midY - 75), new Point(width - 70, midY + 70) };
                    break;
                case 3:
                    for (int i = 0; i <= 80; i++) guide.Points.Add(new Point(width / 2 - 95 + Math.Cos(i / 80.0 * Math.PI * 2) * 70, midY + Math.Sin(i / 80.0 * Math.PI * 2) * 58));
                    for (int i = 0; i <= 80; i++) guide.Points.Add(new Point(width / 2 + 95 + Math.Cos(i / 80.0 * Math.PI * 2) * 70, midY + Math.Sin(i / 80.0 * Math.PI * 2) * 58));
                    break;
                case 4:
                    guide.Points = new PointCollection { new Point(width / 2, midY - 95), new Point(width / 2 + 48, midY - 18), new Point(width / 2 + 135, midY - 18), new Point(width / 2 + 64, midY + 34), new Point(width / 2 + 92, midY + 112), new Point(width / 2, midY + 64), new Point(width / 2 - 92, midY + 112), new Point(width / 2 - 64, midY + 34), new Point(width / 2 - 135, midY - 18), new Point(width / 2 - 48, midY - 18), new Point(width / 2, midY - 95) };
                    break;
                default:
                    for (int i = 0; i <= 80; i++) guide.Points.Add(new Point(70 + (width - 190) * i / 80, midY - 72 + i * 1.25));
                    for (int i = 0; i <= 70; i++) guide.Points.Add(new Point(width - 125 + Math.Cos(i / 70.0 * Math.PI * 1.25) * 64, midY + 28 + Math.Sin(i / 70.0 * Math.PI * 1.25) * 64));
                    break;
            }
            GestureGuideCanvas.Children.Add(guide);
        }

        private Polyline CreateGuideLine()
        {
            return new Polyline
            {
                Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#8DB5FF")),
                StrokeThickness = 6,
                StrokeDashArray = new DoubleCollection { 6, 6 },
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round
            };
        }

        private sealed record MotionPoint(Point Position, double TimeMs);

        private sealed record MotionMetrics(
            double PathLength,
            double DurationMs,
            double AverageSpeed,
            double SpeedInstability,
            double Jitter,
            double DirectionNoise);
    }
}