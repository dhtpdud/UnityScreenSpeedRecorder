using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace UnityCamcorderGui
{
    // 설정을 고르고 UnityCamcorder.exe를 자식 프로세스로 실행한 뒤, 콘솔 출력을 읽어 진행 상황을 보여 줍니다.
    public sealed class MainForm : Form
    {
        private static readonly string[] Codecs =
            { "auto", "libx264", "h264_nvenc", "hevc_nvenc", "h264_qsv", "h264_amf", "libvpx-vp9", "libsvtav1" };
        private static readonly string[] VideoExtensions = { ".mp4", ".mkv", ".webm", ".mov" };

        // 캠코더 콘솔 출력 형식 (RecordingSession.Log)
        private static readonly Regex ProgressLine = new Regex(@"진행: (\d+)(?:/(\d+))? 프레임");
        private static readonly Regex SavedLine = new Regex(@"영상 저장 완료: (.+?) \(");
        private static readonly Regex FailLine = new Regex(@"(?:실패|오류): (.*)");

        private readonly GuiSettings _settings = GuiSettings.Load();

        // --- 대상 ---
        private readonly RadioButton _launchRadio = new RadioButton { Text = "앱을 실행해서 녹화", AutoSize = true };
        private readonly TextBox _exeBox = new TextBox { Dock = DockStyle.Fill };
        private readonly Button _exeBrowse = new Button { Text = "찾아보기...", AutoSize = true };
        private readonly TextBox _argsBox = new TextBox { Dock = DockStyle.Fill };
        private readonly RadioButton _attachRadio = new RadioButton { Text = "이미 켜진 앱에 연결", AutoSize = true };
        private readonly ComboBox _processCombo = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly Button _processRefresh = new Button { Text = "새로고침", AutoSize = true };

        // --- 영상 ---
        private readonly TextBox _outputBox = new TextBox { Dock = DockStyle.Fill };
        private readonly Button _outputBrowse = new Button { Text = "찾아보기...", AutoSize = true };
        private readonly NumericUpDown _fpsBox = new NumericUpDown { Minimum = 1, Maximum = 1000, Width = 80 };
        private readonly RadioButton _secondsRadio = new RadioButton { Text = "초", AutoSize = true };
        private readonly NumericUpDown _secondsBox = new NumericUpDown { Minimum = 0.1m, Maximum = 86400, DecimalPlaces = 1, Increment = 1, Width = 80 };
        private readonly RadioButton _framesRadio = new RadioButton { Text = "프레임", AutoSize = true };
        private readonly NumericUpDown _framesBox = new NumericUpDown { Minimum = 1, Maximum = 100000000, Width = 100 };
        private readonly RadioButton _untilEndRadio = new RadioButton { Text = "중지할 때까지", AutoSize = true };
        private readonly ComboBox _codecCombo = new ComboBox { Width = 140, DropDownStyle = ComboBoxStyle.DropDown };
        private readonly NumericUpDown _qualityBox = new NumericUpDown { Minimum = 0, Maximum = 63, Width = 60 };
        private readonly TextBox _ffmpegBox = new TextBox { Dock = DockStyle.Fill };
        private readonly Button _ffmpegBrowse = new Button { Text = "찾아보기...", AutoSize = true };
        private readonly CheckBox _closeTargetCheck = new CheckBox { Text = "녹화가 끝나면 녹화한 앱을 닫기", AutoSize = true };

        // --- 실행 ---
        private readonly Button _startButton = new Button { Text = "● 녹화 시작", AutoSize = true, Padding = new Padding(10, 4, 10, 4) };
        private readonly Button _stopButton = new Button { Text = "■ 중지", AutoSize = true, Padding = new Padding(10, 4, 10, 4), Enabled = false };
        private readonly Button _openVideoButton = new Button { Text = "영상 열기", AutoSize = true, Enabled = false };
        private readonly Button _openFolderButton = new Button { Text = "저장 폴더 열기", AutoSize = true, Enabled = false };
        private readonly ProgressBar _progress = new ProgressBar { Dock = DockStyle.Fill, Height = 22 };
        private readonly Label _statusLabel = new Label { AutoSize = true, Text = "준비됨", Font = new Font("Malgun Gothic", 10f, FontStyle.Bold) };
        private readonly Label _detailLabel = new Label { AutoSize = true, Text = "" };
        private readonly TextBox _logBox = new TextBox
        {
            Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
            Font = new Font("Consolas", 9f), BackColor = SystemColors.Window, WordWrap = false,
        };
        private readonly Timer _clock = new Timer { Interval = 500 };

        // --- 녹화 상태 ---
        private Process? _camcorder;
        private bool _stopSent;
        private bool _closeAfterExit;
        private int _totalFrames;
        private long _capturedFrames;
        private int _runFps;
        private string? _savedPath;
        private string? _failure;
        private DateTime _startedAt;
        private DateTime? _encodingStartedAt;

        public MainForm()
        {
            Text = "UnityCamcorder — 유니티 앱 배속 녹화";
            Font = new Font("Malgun Gothic", 9f);
            AutoScaleMode = AutoScaleMode.Font;
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(720, 640);
            Size = new Size(820, 760);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            BuildLayout();
            LoadSettingsToUi();
            WireEvents();
            UpdateEnabledState();
            RefreshProcesses();
        }

        // ------------------------------------------------------------------ 화면 구성

        private void BuildLayout()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(10) };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            // 1. 녹화할 앱
            var target = NewGrid();
            AddRow(target, _launchRadio, _exeBox, _exeBrowse);
            AddRow(target, Indent(new Label { Text = "실행 인자 (선택)", AutoSize = true, Anchor = AnchorStyles.Left }), _argsBox, null);
            AddRow(target, _attachRadio, _processCombo, _processRefresh);
            root.Controls.Add(Group("1. 녹화할 유니티 앱", target));

            // 2. 결과 영상
            var video = NewGrid();
            AddRow(video, Caption("저장할 파일"), _outputBox, _outputBrowse);
            AddRow(video, Caption("FPS"), Flow(_fpsBox, Hint("결과 영상의 초당 프레임 수")), null);
            AddRow(video, Caption("길이"), Flow(_secondsRadio, _secondsBox, Gap(), _framesRadio, _framesBox, Gap(), _untilEndRadio), null);
            AddRow(video, Caption("코덱"), Flow(_codecCombo, Hint("auto = 자동 선택(추천)"), Gap(),
                new Label { Text = "화질", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 3, 0) },
                _qualityBox, Hint("낮을수록 고화질")), null);
            AddRow(video, Caption("ffmpeg.exe"), _ffmpegBox, _ffmpegBrowse);
            AddRow(video, new Label(), Hint("비워 두면 UnityCamcorder.exe 옆 → PATH 순으로 자동으로 찾습니다."), null);
            AddRow(video, new Label(), _closeTargetCheck, null);
            root.Controls.Add(Group("2. 결과 영상", video));

            // 실행 버튼 + 진행
            var run = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, AutoSize = true };
            run.Controls.Add(Flow(_startButton, _stopButton, Gap(), _openVideoButton, _openFolderButton));
            run.Controls.Add(_progress);
            run.Controls.Add(_statusLabel);
            run.Controls.Add(_detailLabel);
            root.Controls.Add(run);

            // 로그
            root.Controls.Add(Group("기록", _logBox, fill: true));

            Controls.Add(root);
        }

        private static TableLayoutPanel NewGrid()
        {
            var grid = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 3, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            return grid;
        }

        private static void AddRow(TableLayoutPanel grid, Control label, Control field, Control? button)
        {
            int row = grid.RowCount++;
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.Controls.Add(label, 0, row);
            grid.Controls.Add(field, 1, row);
            if (button != null) grid.Controls.Add(button, 2, row);
        }

        private static GroupBox Group(string title, Control content, bool fill = false)
        {
            var box = new GroupBox { Text = title, Dock = DockStyle.Fill, Padding = new Padding(8) };
            if (!fill) { box.AutoSize = true; box.AutoSizeMode = AutoSizeMode.GrowAndShrink; }
            box.Controls.Add(content);
            return box;
        }

        private static FlowLayoutPanel Flow(params Control[] controls)
        {
            var flow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0) };
            flow.Controls.AddRange(controls);
            return flow;
        }

        private static Label Caption(string text) =>
            new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 10, 0) };

        private static Label Hint(string text) =>
            new Label { Text = text, AutoSize = true, ForeColor = SystemColors.GrayText, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 3, 0) };

        private static Control Gap() => new Label { Width = 16, Text = "" };

        private static Control Indent(Control control)
        {
            control.Margin = new Padding(20, 7, 10, 0);
            return control;
        }

        // ------------------------------------------------------------------ 설정 ↔ 화면

        private void LoadSettingsToUi()
        {
            _codecCombo.Items.AddRange(Codecs);

            _launchRadio.Checked = !_settings.AttachToRunning;
            _attachRadio.Checked = _settings.AttachToRunning;
            _exeBox.Text = _settings.TargetExe;
            _argsBox.Text = _settings.TargetArgs;
            _outputBox.Text = string.IsNullOrWhiteSpace(_settings.OutputPath) ? DefaultOutputPath() : _settings.OutputPath;
            _fpsBox.Value = Clamp(_settings.Fps, _fpsBox);
            _secondsBox.Value = Clamp((decimal)_settings.DurationSeconds, _secondsBox);
            _framesBox.Value = Clamp(_settings.TotalFrames, _framesBox);
            _secondsRadio.Checked = _settings.LengthMode == 0;
            _framesRadio.Checked = _settings.LengthMode == 1;
            _untilEndRadio.Checked = _settings.LengthMode == 2;
            _codecCombo.Text = string.IsNullOrWhiteSpace(_settings.Codec) ? "auto" : _settings.Codec;
            _qualityBox.Value = Clamp(_settings.Quality, _qualityBox);
            _ffmpegBox.Text = _settings.FfmpegPath;
            _closeTargetCheck.Checked = _settings.CloseTargetWhenDone;
        }

        private void SaveUiToSettings()
        {
            _settings.AttachToRunning = _attachRadio.Checked;
            _settings.TargetExe = _exeBox.Text.Trim();
            _settings.TargetArgs = _argsBox.Text.Trim();
            _settings.OutputPath = _outputBox.Text.Trim();
            _settings.Fps = (int)_fpsBox.Value;
            _settings.LengthMode = _secondsRadio.Checked ? 0 : _framesRadio.Checked ? 1 : 2;
            _settings.DurationSeconds = (double)_secondsBox.Value;
            _settings.TotalFrames = (int)_framesBox.Value;
            _settings.Codec = string.IsNullOrWhiteSpace(_codecCombo.Text) ? "auto" : _codecCombo.Text.Trim();
            _settings.Quality = (int)_qualityBox.Value;
            _settings.FfmpegPath = _ffmpegBox.Text.Trim().Trim('"');
            _settings.CloseTargetWhenDone = _closeTargetCheck.Checked;
            _settings.Save();
        }

        private static decimal Clamp(decimal value, NumericUpDown box) => Math.Min(box.Maximum, Math.Max(box.Minimum, value));

        private static string DefaultOutputPath() =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "UnityCamcorder", "recording.mp4");

        // ------------------------------------------------------------------ 이벤트

        private void WireEvents()
        {
            _launchRadio.CheckedChanged += (s, e) => UpdateEnabledState();
            _attachRadio.CheckedChanged += (s, e) => UpdateEnabledState();
            _secondsRadio.CheckedChanged += (s, e) => UpdateEnabledState();
            _framesRadio.CheckedChanged += (s, e) => UpdateEnabledState();
            _untilEndRadio.CheckedChanged += (s, e) => UpdateEnabledState();
            _processCombo.DropDown += (s, e) => { if (_processCombo.Items.Count == 0) RefreshProcesses(); };
            _processCombo.SelectedIndexChanged += (s, e) => { if (_processCombo.SelectedIndex >= 0) _attachRadio.Checked = true; };
            _exeBox.TextChanged += (s, e) => { if (_exeBox.Focused) _launchRadio.Checked = true; };

            _exeBrowse.Click += (s, e) => BrowseExe();
            _processRefresh.Click += (s, e) => RefreshProcesses();
            _outputBrowse.Click += (s, e) => BrowseOutput();
            _ffmpegBrowse.Click += (s, e) => BrowseFfmpeg();
            _startButton.Click += (s, e) => StartRecording();
            _stopButton.Click += (s, e) => StopRecording();
            _openVideoButton.Click += (s, e) => { if (_savedPath != null) ShellOpen(_savedPath, null); };
            _openFolderButton.Click += (s, e) => OpenFolder();
            _clock.Tick += (s, e) => UpdateDetail();

            // 탐색기에서 exe를 끌어다 놓기
            AllowDrop = true;
            DragEnter += (s, e) => e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            DragDrop += (s, e) =>
            {
                if (IsRunning || !(e.Data.GetData(DataFormats.FileDrop) is string[] files) || files.Length == 0) return;
                if (files[0].EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    _exeBox.Text = files[0];
                    _launchRadio.Checked = true;
                }
            };

            FormClosing += OnFormClosing;
        }

        private bool IsRunning => _camcorder != null;

        private void UpdateEnabledState()
        {
            bool idle = !IsRunning;
            foreach (Control c in new Control[] { _launchRadio, _attachRadio, _outputBox, _outputBrowse, _fpsBox, _secondsRadio,
                         _framesRadio, _untilEndRadio, _codecCombo, _qualityBox, _ffmpegBox, _ffmpegBrowse, _closeTargetCheck })
                c.Enabled = idle;
            _exeBox.Enabled = _exeBrowse.Enabled = _argsBox.Enabled = idle && _launchRadio.Checked;
            _processCombo.Enabled = _processRefresh.Enabled = idle && _attachRadio.Checked;
            _secondsBox.Enabled = idle && _secondsRadio.Checked;
            _framesBox.Enabled = idle && _framesRadio.Checked;
            _startButton.Enabled = idle;
            _stopButton.Enabled = !idle;
            _openVideoButton.Enabled = idle && _savedPath != null && File.Exists(_savedPath);
            _openFolderButton.Enabled = idle && !string.IsNullOrWhiteSpace(_outputBox.Text);
        }

        private void BrowseExe()
        {
            using (var dialog = new OpenFileDialog { Filter = "실행 파일 (*.exe)|*.exe", Title = "녹화할 유니티 앱 선택" })
            {
                TrySetInitialDirectory(dialog, _exeBox.Text);
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    _exeBox.Text = dialog.FileName;
                    _launchRadio.Checked = true;
                }
            }
        }

        private void BrowseOutput()
        {
            using (var dialog = new SaveFileDialog
            {
                Filter = "MP4 영상 (*.mp4)|*.mp4|MKV 영상 (*.mkv)|*.mkv|WebM 영상 (*.webm)|*.webm|MOV 영상 (*.mov)|*.mov",
                Title = "영상 저장 위치", OverwritePrompt = false,
            })
            {
                TrySetInitialDirectory(dialog, _outputBox.Text);
                try { dialog.FileName = Path.GetFileName(_outputBox.Text); } catch { }
                string ext = SafeExtension(_outputBox.Text);
                int index = Array.IndexOf(VideoExtensions, ext);
                dialog.FilterIndex = index >= 0 ? index + 1 : 1;
                if (dialog.ShowDialog(this) == DialogResult.OK)
                    _outputBox.Text = dialog.FileName;
            }
        }

        private void BrowseFfmpeg()
        {
            using (var dialog = new OpenFileDialog { Filter = "ffmpeg.exe|ffmpeg.exe|실행 파일 (*.exe)|*.exe", Title = "ffmpeg.exe 선택" })
            {
                TrySetInitialDirectory(dialog, _ffmpegBox.Text);
                if (dialog.ShowDialog(this) == DialogResult.OK)
                    _ffmpegBox.Text = dialog.FileName;
            }
        }

        private static void TrySetInitialDirectory(FileDialog dialog, string path)
        {
            try
            {
                string? dir = Path.GetDirectoryName(path.Trim().Trim('"'));
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) dialog.InitialDirectory = dir;
            }
            catch { }
        }

        private void RefreshProcesses()
        {
            var previous = (_processCombo.SelectedItem as ProcessItem)?.Pid;
            Cursor = Cursors.WaitCursor;
            try
            {
                var items = ProcessList.GetWindowed();
                _processCombo.BeginUpdate();
                _processCombo.Items.Clear();
                foreach (var item in items) _processCombo.Items.Add(item);
                _processCombo.EndUpdate();
                var keep = items.FirstOrDefault(i => i.Pid == previous);
                if (keep != null) _processCombo.SelectedItem = keep;
                else if (items.Count > 0 && items[0].IsUnity) _processCombo.SelectedIndex = 0;
            }
            finally { Cursor = Cursors.Default; }
        }

        // ------------------------------------------------------------------ 녹화 시작/중지

        private void StartRecording()
        {
            string camcorderExe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "UnityCamcorder.exe");
            if (!File.Exists(camcorderExe))
            {
                Warn($"UnityCamcorder.exe를 찾을 수 없습니다.\n이 프로그램과 같은 폴더에 있어야 합니다:\n{camcorderExe}");
                return;
            }

            var config = new RunConfig
            {
                Fps = (int)_fpsBox.Value,
                Codec = string.IsNullOrWhiteSpace(_codecCombo.Text) ? "auto" : _codecCombo.Text.Trim(),
                Quality = (int)_qualityBox.Value,
                CloseTargetWhenDone = _closeTargetCheck.Checked,
            };

            // 대상
            if (_launchRadio.Checked)
            {
                string exe = _exeBox.Text.Trim().Trim('"');
                if (exe.Length == 0) { Warn("녹화할 유니티 앱(exe)을 골라 주세요."); return; }
                if (!File.Exists(exe)) { Warn($"앱 파일을 찾을 수 없습니다:\n{exe}"); return; }
                config.TargetExe = exe;
                config.TargetArgs = _argsBox.Text.Trim().Length > 0 ? _argsBox.Text.Trim() : null;
            }
            else
            {
                if (!(_processCombo.SelectedItem is ProcessItem item)) { Warn("연결할 앱을 목록에서 골라 주세요. (목록이 비어 있으면 새로고침)"); return; }
                try { using (var p = Process.GetProcessById(item.Pid)) if (p.HasExited) throw new InvalidOperationException(); }
                catch { Warn("고른 앱이 이미 종료되었습니다. 새로고침 후 다시 골라 주세요."); RefreshProcesses(); return; }
                config.TargetPid = item.Pid;
            }

            // 저장 경로
            string output = _outputBox.Text.Trim().Trim('"');
            if (output.Length == 0) { Warn("영상을 저장할 파일 경로를 정해 주세요."); return; }
            try
            {
                output = Path.GetFullPath(output);
                if (!VideoExtensions.Contains(SafeExtension(output)))
                    output += ".mp4";
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            }
            catch (Exception ex) { Warn($"저장 경로가 올바르지 않습니다:\n{ex.Message}"); return; }
            if (File.Exists(output) &&
                MessageBox.Show(this, $"이미 같은 이름의 파일이 있습니다. 덮어쓸까요?\n{output}", Text,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            _outputBox.Text = output;
            config.OutputPath = output;

            // 길이
            if (_secondsRadio.Checked) config.DurationSeconds = (double)_secondsBox.Value;
            else if (_framesRadio.Checked) config.TotalFrames = (int)_framesBox.Value;
            _totalFrames = config.TotalFrames > 0 ? config.TotalFrames
                : config.DurationSeconds > 0 ? (int)Math.Round(config.DurationSeconds * config.Fps) : 0;

            // ffmpeg (캠코더와 같은 순서로 미리 확인해 바로 알려 줌)
            string ffmpeg = _ffmpegBox.Text.Trim().Trim('"');
            if (ffmpeg.Length > 0)
            {
                if (!File.Exists(ffmpeg)) { Warn($"ffmpeg.exe를 찾을 수 없습니다:\n{ffmpeg}"); return; }
                config.FfmpegPath = ffmpeg;
            }
            else if (!FfmpegFoundAutomatically(Path.GetDirectoryName(camcorderExe)!))
            {
                Warn("ffmpeg.exe를 찾을 수 없습니다.\n\nffmpeg.exe를 UnityCamcorder.exe 옆에 두거나, 'ffmpeg.exe' 칸에서 직접 골라 주세요.");
                return;
            }

            SaveUiToSettings();

            string configPath;
            try { configPath = config.Write(); }
            catch (Exception ex) { Warn($"설정 파일을 쓸 수 없습니다:\n{ex.Message}"); return; }

            var psi = new ProcessStartInfo(camcorderExe, $"--config \"{configPath}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Path.GetDirectoryName(camcorderExe)!,
            };

            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.OutputDataReceived += (s, e) => { if (e.Data != null) Post(() => OnLine(e.Data, false)); };
            process.ErrorDataReceived += (s, e) => { if (e.Data != null) Post(() => OnLine(e.Data, true)); };
            process.Exited += (s, e) =>
            {
                // 남은 출력을 모두 받은 뒤 마무리
                try { process.WaitForExit(); } catch { }
                Post(() => OnExited(process));
            };

            ResetRunState(config.Fps);
            _logBox.Clear();
            AppendLog($"녹화 준비: {(config.TargetExe ?? $"PID {config.TargetPid}")} → {output}");
            try
            {
                process.Start();
            }
            catch (Exception ex)
            {
                Warn($"UnityCamcorder.exe를 실행하지 못했습니다:\n{ex.Message}");
                return;
            }
            _camcorder = process;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            SetStatus(_launchRadio.Checked ? "앱을 실행하고 연결하는 중..." : "앱에 연결하는 중...", SystemColors.ControlText);
            _progress.Style = ProgressBarStyle.Marquee;
            _clock.Start();
            UpdateEnabledState();
        }

        private void StopRecording()
        {
            var process = _camcorder;
            if (process == null) return;

            if (!_stopSent)
            {
                // 캠코더에 "stop" 전달 → 지금까지 찍은 프레임으로 영상을 정상 마무리
                _stopSent = true;
                try
                {
                    process.StandardInput.WriteLine("stop");
                    process.StandardInput.Flush();
                }
                catch { }
                SetStatus("중지하는 중... 영상을 마무리하고 있습니다.", SystemColors.ControlText);
                _stopButton.Text = "강제 종료";
                AppendLog("중지 요청을 보냈습니다.");
                return;
            }

            if (MessageBox.Show(this, "강제로 끝내면 영상이 덜 저장될 수 있습니다. 강제 종료할까요?", Text,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;
            KillTree(process);
        }

        private static void KillTree(Process process)
        {
            try
            {
                // 캠코더가 띄운 ffmpeg까지 함께 종료
                using (var kill = Process.Start(new ProcessStartInfo("taskkill", $"/T /F /PID {process.Id}")
                       { UseShellExecute = false, CreateNoWindow = true }))
                    kill?.WaitForExit(5000);
            }
            catch { }
            try { if (!process.HasExited) process.Kill(); } catch { }
        }

        private void ResetRunState(int fps)
        {
            _stopSent = false;
            _capturedFrames = 0;
            _runFps = fps;
            _savedPath = null;
            _failure = null;
            _startedAt = DateTime.Now;
            _encodingStartedAt = null;
            _stopButton.Text = "■ 중지";
            _progress.Value = 0;
            _detailLabel.Text = "";
        }

        // ------------------------------------------------------------------ 캠코더 출력 처리

        private void OnLine(string line, bool isError)
        {
            AppendLog(isError ? "! " + line : line);

            var progress = ProgressLine.Match(line);
            if (progress.Success)
            {
                _capturedFrames = long.Parse(progress.Groups[1].Value, CultureInfo.InvariantCulture);
                if (progress.Groups[2].Success) _totalFrames = int.Parse(progress.Groups[2].Value, CultureInfo.InvariantCulture);
                UpdateProgress();
                return;
            }
            if (line.Contains("후킹 완료"))
            {
                if (!_stopSent) SetStatus("녹화 중...", Color.Firebrick);
                return;
            }
            if (line.Contains("인코딩 시작"))
            {
                _encodingStartedAt = DateTime.Now;
                UpdateProgress();
                return;
            }
            if (line.Contains("경고:"))
            {
                SetStatus("녹화 중 (경고 있음, 아래 기록 참고)", Color.DarkOrange);
                return;
            }
            var saved = SavedLine.Match(line);
            if (saved.Success)
            {
                _savedPath = saved.Groups[1].Value.Trim();
                return;
            }
            var fail = FailLine.Match(line);
            if (fail.Success)
                _failure = fail.Groups[1].Value.Trim();
        }

        private void OnExited(Process process)
        {
            if (!ReferenceEquals(process, _camcorder)) return;
            int code;
            try { code = process.ExitCode; } catch { code = -1; }
            process.Dispose();
            _camcorder = null;
            _clock.Stop();
            _progress.Style = ProgressBarStyle.Continuous;

            if (code == 0 && _savedPath != null)
            {
                if (_totalFrames > 0 && _capturedFrames < _totalFrames && !_stopSent)
                    SetStatus($"저장 완료 (목표보다 짧게 끝남: {_capturedFrames}/{_totalFrames} 프레임)", Color.DarkOrange);
                else
                    SetStatus("저장 완료!", Color.ForestGreen);
                if (_totalFrames <= 0 || _capturedFrames >= _totalFrames) _progress.Value = _progress.Maximum;
                _detailLabel.Text = _savedPath;
            }
            else if (code == 0)
            {
                SetStatus("끝났습니다. (아래 기록을 확인하세요)", SystemColors.ControlText);
            }
            else
            {
                SetStatus("녹화 실패" + (_failure != null ? ": " + _failure : $" (종료 코드 {code})"), Color.Firebrick);
            }
            AppendLog($"캠코더 종료 (코드 {code})");
            UpdateEnabledState();

            if (_closeAfterExit) Close();
        }

        private void UpdateProgress()
        {
            if (_progress.Style == ProgressBarStyle.Marquee && _totalFrames > 0)
                _progress.Style = ProgressBarStyle.Continuous;
            if (_totalFrames > 0)
            {
                _progress.Maximum = _totalFrames;
                _progress.Value = (int)Math.Min(_totalFrames, _capturedFrames);
            }
            UpdateDetail();
        }

        private void UpdateDetail()
        {
            if (!IsRunning) return;
            var elapsed = DateTime.Now - _startedAt;
            var sb = new StringBuilder($"경과 {elapsed:mm\\:ss}");
            if (_capturedFrames > 0)
            {
                sb.Append($"  ·  {_capturedFrames:N0}");
                if (_totalFrames > 0) sb.Append($" / {_totalFrames:N0} 프레임 ({100.0 * _capturedFrames / _totalFrames:0}%)");
                else sb.Append(" 프레임");
                double videoSeconds = (double)_capturedFrames / _runFps;
                sb.Append($"  ·  영상 {videoSeconds:0.0}초");
                if (_encodingStartedAt.HasValue)
                {
                    double real = (DateTime.Now - _encodingStartedAt.Value).TotalSeconds;
                    if (real > 0.5) sb.Append($"  ·  {videoSeconds / real:0.0}배속");
                }
            }
            _detailLabel.Text = sb.ToString();
        }

        // ------------------------------------------------------------------ 기타

        private void OnFormClosing(object? sender, FormClosingEventArgs e)
        {
            if (!IsRunning)
            {
                SaveUiToSettings();
                return;
            }
            if (_closeAfterExit)
            {
                e.Cancel = true;
                return;
            }
            if (MessageBox.Show(this, "녹화 중입니다. 지금까지 찍은 영상을 저장하고 닫을까요?", Text,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            e.Cancel = true;
            _closeAfterExit = true;
            if (!_stopSent) StopRecording();
        }

        private void OpenFolder()
        {
            string target = _savedPath ?? _outputBox.Text.Trim().Trim('"');
            try
            {
                if (File.Exists(target)) { ShellOpen("explorer.exe", $"/select,\"{target}\""); return; }
                string? dir = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) ShellOpen(dir, null);
                else Warn("저장 폴더가 아직 없습니다.");
            }
            catch (Exception ex) { Warn(ex.Message); }
        }

        private void ShellOpen(string file, string? args)
        {
            try { Process.Start(new ProcessStartInfo(file, args ?? "") { UseShellExecute = true })?.Dispose(); }
            catch (Exception ex) { Warn(ex.Message); }
        }

        private static bool FfmpegFoundAutomatically(string camcorderDir)
        {
            if (File.Exists(Path.Combine(camcorderDir, "ffmpeg.exe")) || File.Exists(Path.Combine(camcorderDir, "ffmpeg", "ffmpeg.exe")))
                return true;
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                try { if (File.Exists(Path.Combine(dir.Trim().Trim('"'), "ffmpeg.exe"))) return true; }
                catch { }
            }
            return false;
        }

        private static string SafeExtension(string path)
        {
            try { return Path.GetExtension(path.Trim().Trim('"')).ToLowerInvariant(); }
            catch { return ""; }
        }

        private void SetStatus(string text, Color color)
        {
            _statusLabel.Text = text;
            _statusLabel.ForeColor = color;
        }

        private void AppendLog(string line)
        {
            if (_logBox.TextLength > 300_000)
                _logBox.Text = _logBox.Text.Substring(_logBox.TextLength - 150_000);
            _logBox.AppendText(line + Environment.NewLine);
        }

        private void Post(Action action)
        {
            try { if (!IsDisposed) BeginInvoke(action); }
            catch (InvalidOperationException) { /* 창이 닫히는 중 */ }
        }

        private void Warn(string message) => MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
}
