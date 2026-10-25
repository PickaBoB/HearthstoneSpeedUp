using System.Diagnostics;

namespace GameSpeed;

/// <summary>
/// Hearthstone-only window: waits for the game, attaches the Unity module as
/// soon as the game's scripting runtime is up, and switches between presets.
/// Speed-up always applies only while actions play out (see gsunity.c).
/// </summary>
public sealed class MainForm : Form
{
    private const string ProcessName = "Hearthstone";
    private static readonly double[] Presets = { 1, 2, 3, 8 };
    private const SharedCtl.Flags PublishFlags = SharedCtl.Flags.Selective;

    private readonly Label    _target = new();
    private readonly Label    _state  = new();
    private readonly Label    _actual = new();
    private readonly Button[] _presetButtons = new Button[Presets.Length];
    private readonly System.Windows.Forms.Timer _poll = new();

    private SharedCtl? _ctl;
    private double _speed = 1.0;
    private bool   _userChose;
    // A pid that refused injection (no rights, wrong bitness) is not retried
    // every tick; a restarted game gets a new pid and a fresh attempt.
    private int    _failedPid;

    public MainForm()
    {
        Text = "Hearthstone SpeedUp";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        ClientSize = new Size(340, 270);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9f);

        BuildLayout();
        RegisterHotkeys();
        HighlightPreset();

        _poll.Interval = 400;
        _poll.Tick += (_, _) => Tick();
        _poll.Start();
        Tick();
    }

    private void BuildLayout()
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(12),
        };
        Controls.Add(panel);

        _target.AutoSize = false;
        _target.Size = new Size(310, 22);
        _target.Font = new Font(Font, FontStyle.Bold);

        _state.AutoSize = false;
        _state.Size = new Size(310, 36);
        _state.ForeColor = SystemColors.GrayText;

        var speedBox = new GroupBox { Text = "Скорость боя и розыгрыша", Size = new Size(310, 70) };
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = Presets.Length, RowCount = 1,
            Padding = new Padding(4),
        };
        for (int i = 0; i < Presets.Length; i++)
        {
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / Presets.Length));
            double v = Presets[i];
            var b = new Button
            {
                Text = $"x{v:0}",
                Dock = DockStyle.Fill,
                Margin = new Padding(3),
                Font = new Font("Segoe UI", 11f, FontStyle.Bold),
            };
            b.Click += (_, _) => { _userChose = true; SetSpeed(v); };
            _presetButtons[i] = b;
            row.Controls.Add(b, i, 0);
        }
        speedBox.Controls.Add(row);

        _actual.AutoSize = false;
        _actual.Size = new Size(310, 40);
        _actual.ForeColor = SystemColors.GrayText;
        _actual.Margin = new Padding(3, 8, 3, 0);

        var help = new Label
        {
            AutoSize = false, Size = new Size(310, 50),
            ForeColor = SystemColors.GrayText,
            Text = "Ускоряются только бой и розыгрыш, остальное идёт как обычно.\n"
                 + "Горячие клавиши: Ctrl+Alt+1..4 — x1, x2, x3, x8.",
            Margin = new Padding(3, 8, 3, 0),
        };

        panel.Controls.AddRange(new Control[] { _target, _state, speedBox, _actual, help });
    }

    // ---- attaching ---------------------------------------------------

    private static Process? FindGame()
    {
        Process[] all = Process.GetProcessesByName(ProcessName);
        Process? pick = null;
        foreach (Process p in all)
        {
            if (pick is null) pick = p;
            else p.Dispose();
        }
        return pick;
    }

    private void Tick()
    {
        if (_ctl is not null)
        {
            if (IsAlive(_ctl.Pid)) { UpdateStatus(); return; }
            _ctl.Dispose();
            _ctl = null;
        }

        using Process? game = FindGame();
        if (game is null)
        {
            _target.Text = "Hearthstone не запущен";
            _state.Text = "Подключусь автоматически, как только игра запустится.";
            _actual.Text = "";
            return;
        }

        int pid = game.Id;
        _target.Text = $"Hearthstone  ·  PID {pid}";
        if (pid == _failedPid) return;

        // Injecting into a game that is still starting is what crashed it;
        // the module waits for the engine itself, this just keeps the
        // injection out of the first seconds of the launch altogether.
        if (!LooksStarted(game))
        {
            _state.Text = "Игра запускается, жду…";
            return;
        }

        // The module waits for the runtime itself, but attaching only once
        // Mono is in keeps it from timing out on a slow start.
        switch (Injector.DetectUnity(pid))
        {
            case UnityKind.Mono:
            case UnityKind.Il2Cpp:
                break;
            case UnityKind.Unreadable:
                _state.Text = "Модули игры не читаются. Если игра запущена от администратора, "
                            + "запустите GameSpeed тоже от администратора.";
                return;
            default:
                _state.Text = "Игра запускается, жду рантайм Unity…";
                return;
        }

        Attach(pid);
    }

    private void Attach(int pid)
    {
        try
        {
            // The block has to exist before the DLL arrives: the DLL only
            // opens it, so its permissions stay ours.
            _ctl = SharedCtl.Create(pid);

            if (_ctl.Attached)
            {
                // Hooked by an earlier run of this window: take over, keeping
                // the game's speed unless a preset was picked already.
                if (!_userChose) _speed = _ctl.Multiplier;
                Publish();
                HighlightPreset();
            }
            else
            {
                Injector.Inject(pid, Injector.DllPath(Method.Unity));
                Publish();
                _state.Text = "Жду, пока игра загрузится…";
            }
        }
        catch (Exception ex)
        {
            _ctl?.Dispose();
            _ctl = null;
            _failedPid = pid;
            _state.Text = "Не удалось подключиться: " + ex.Message.Split('\n')[0];
        }
    }

    private static bool LooksStarted(Process game)
    {
        try
        {
            return DateTime.Now - game.StartTime >= TimeSpan.FromSeconds(10)
                && game.MainWindowHandle != IntPtr.Zero;
        }
        catch { return true; }   // unreadable: let the module's own wait decide
    }

    private static bool IsAlive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch { return false; }
    }

    // ---- speed -------------------------------------------------------

    private void SetSpeed(double v)
    {
        _speed = v;
        Publish();
        HighlightPreset();
    }

    private void Publish() => _ctl?.Write(_speed, PublishFlags);

    private void HighlightPreset()
    {
        for (int i = 0; i < Presets.Length; i++)
        {
            bool on = Math.Abs(Presets[i] - _speed) < 0.001;
            Button b = _presetButtons[i];
            if (on)
            {
                b.BackColor = SystemColors.Highlight;
                b.ForeColor = SystemColors.HighlightText;
            }
            else
            {
                b.UseVisualStyleBackColor = true;
                b.ForeColor = SystemColors.ControlText;
            }
        }
    }

    private void UpdateStatus()
    {
        if (_ctl is null) return;

        if (_ctl.GaveUp)
        {
            _state.Text = $"Рантайм Unity не найден · см. %TEMP%\\gsunity_{_ctl.Pid}.log";
            _actual.Text = "";
            return;
        }
        if (!_ctl.Attached)
        {
            _state.Text = "Жду, пока игра загрузится…";
            return;
        }

        var hs = _ctl.Hearthstone;
        if (!hs.HasFlag(SharedCtl.HsState.Active))
        {
            _state.Text = "Подключено · жду игровые системы Hearthstone…";
        }
        else if (!hs.HasFlag(SharedCtl.HsState.Selective))
        {
            _state.Text = "Подключено · избирательный режим недоступен в этой версии игры, "
                        + "ускоряется всё";
        }
        else
        {
            _state.Text = "Подключено";
        }

        string phase = hs.HasFlag(SharedCtl.HsState.Selective)
            ? (hs.HasFlag(SharedCtl.HsState.Fast) ? "идёт действие" : "простой, x1")
            : "";
        _actual.Text = $"Сейчас: x{_ctl.Actual:0.00}" + (phase.Length > 0 ? $"  ({phase})" : "");
    }

    // ---- hotkeys -----------------------------------------------------

    private void RegisterHotkeys()
    {
        for (int i = 0; i < Presets.Length; i++)
        {
            // Ctrl+Alt+1..4; a failure here is not worth blocking startup.
            Native.RegisterHotKey(Handle, 100 + i,
                Native.MOD_CONTROL | Native.MOD_ALT, (int)Keys.D1 + i);
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_HOTKEY)
        {
            int id = m.WParam.ToInt32() - 100;
            if (id >= 0 && id < Presets.Length) { _userChose = true; SetSpeed(Presets[id]); }
        }
        base.WndProc(ref m);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        for (int i = 0; i < Presets.Length; i++)
            Native.UnregisterHotKey(Handle, 100 + i);
        _poll.Stop();
        _ctl?.Dispose();
        base.OnFormClosed(e);
    }
}
