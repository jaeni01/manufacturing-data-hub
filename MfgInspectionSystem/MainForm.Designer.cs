using MfgInspectionSystem.UI;
using MfgInspectionSystem.UI.Controls;
using WebView2WinForms = Microsoft.Web.WebView2.WinForms;

namespace MfgInspectionSystem;

partial class MainForm
{
    private System.ComponentModel.IContainer components = null;

    // ── Top-bar / status ──
    private Label lblMqttStatus;
    private Label lblSerialStatus;
    private Label lblRpiStatus;
    private Label lblYoloStatus;
    private Label lblDbStatus;
    private Label lblSystemState;

    // ── Operational buttons ──
    private Button btnStart;
    private Button btnStop;
    private Button btnResume;
    private Button btnEmergency;
    private Button btnReset;
    private Button btnYoloHealth;
    private Button btnDbTest;
    private Button btnClearStats;
    private Button btnAuditVerify;

    // ── Environment ──
    private Label lblTemp;
    private Label lblHumidity;
    private Label lblGas;

    // ── Production stats ──
    private Label lblTotal;
    private Label lblPass;
    private Label lblDefect;
    private Label lblHold;
    private Label lblQueueDepth;   // orphan — field kept for null-safety, not displayed
    private Label lblPassRate;

    // ── Last inspection ──
    private Label      lblLastId;
    private Label      lblLastType;
    private Label      lblLastVerdict;
    private Label      lblLastYoloClass;
    private Label      lblLastConf;
    private Label      lblLastPins;
    private Label      lblLastBlur;
    private Label      lblLastImage;
    private Label      lblLastTime;
    private PictureBox picLastImage;
    private Panel      pnlVerdictBadge;

    // ── Node status rows ──
    private NodeStatusRow nodeMqtt;
    private NodeStatusRow nodeSerial;
    private NodeStatusRow nodeRpi;
    private NodeStatusRow nodeYolo;
    private NodeStatusRow nodeDb;

    // ── Environment metric cards ──
    private MetricCard metricTemp;
    private MetricCard metricHum;
    private MetricCard metricGas;

    // ── Live camera view ──
    private WebView2WinForms.WebView2 webCam1Live;
    private WebView2WinForms.WebView2 webCam2Live;
    private Label lblCam1Time;

    // ── Process flow (dynamic) ──
#pragma warning disable CS8669
    private ProcessFlowView? _processFlow;
#pragma warning restore CS8669

    // ── Result card (new layout per guide Step 4) ──
    private Label      lblResultBigVerdict;
    private PictureBox picResultThumb;
    private Label      lblResultId;
    private Label      lblResultType;
    private Label      lblResultYolo;
    private Label      lblResultConf;
    private Label      lblResultPins;
    private Label      lblResultBlur;

    // ── Log tabs ──
    private TabControl  tabLogs;
    private TabPage     tabEventLog;
    private TabPage     tabSerialLog;
    private TabPage     tabMqttLog;
    private RichTextBox rtbEventLog;
    private RichTextBox rtbSerialLog;
    private RichTextBox rtbMqttLog;

    // ── Clock timer ──
    private System.Windows.Forms.Timer _clockTimer;

    protected override void Dispose(bool disposing)
    {
        if (disposing && components != null) components.Dispose();
        base.Dispose(disposing);
    }

    private void InitializeComponent()
    {
        components = new System.ComponentModel.Container();
        SuspendLayout();

        // ── Form ──
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode       = AutoScaleMode.Dpi;
        ClientSize          = new Size(1536, 1024);
        MinimumSize         = new Size(1280, 820);
        Text                = "산업 제조 검사 시스템 — WinForms Control v8.0";
        BackColor           = DesignTokens.BgWindow;
        Font                = DesignTokens.FontBody;
        Load         += MainForm_Load;
        FormClosing  += MainForm_FormClosing;

        // ── Clock ──
        _clockTimer = new System.Windows.Forms.Timer(components) { Interval = 1000, Enabled = true };

        // ── Log tabs (Bottom) ──
        rtbEventLog  = MakeRtb();
        rtbSerialLog = MakeRtb();
        rtbMqttLog   = MakeRtb();

        tabEventLog  = new TabPage("이벤트 로그");    tabEventLog.Controls.Add(rtbEventLog);
        tabSerialLog = new TabPage("Serial Monitor"); tabSerialLog.Controls.Add(rtbSerialLog);
        tabMqttLog   = new TabPage("MQTT Monitor");   tabMqttLog.Controls.Add(rtbMqttLog);

        tabLogs = new TabControl
        {
            Dock      = DockStyle.Bottom,
            Height    = 175,
            Font      = DesignTokens.FontBody,
            BackColor = DesignTokens.BgCard,
        };
        tabLogs.TabPages.AddRange(new[] { tabEventLog, tabSerialLog, tabMqttLog });

        // ── Main 4×3 grid ──
        var mainGrid = new TableLayoutPanel
        {
            Dock            = DockStyle.Fill,
            ColumnCount     = 4,
            RowCount        = 3,
            BackColor       = DesignTokens.BgWindow,
            Padding         = new Padding(DesignTokens.SpacingSm),
            CellBorderStyle = TableLayoutPanelCellBorderStyle.None,
        };
        for (int i = 0; i < 4; i++)
            mainGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25f));
        mainGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 240));  // Row 0: 4 cards
        mainGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 128));  // Row 1: process flow
        mainGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));  // Row 2: vision / env / detail

        // Row 0
        var cardCtrl = BuildControlCard();
        var cardNode = BuildNodeStatusCard();
        var cardProd = BuildProductionCard();
        var cardQueue = BuildQueueCard();
        mainGrid.Controls.Add(cardCtrl,  0, 0);
        mainGrid.Controls.Add(cardNode,  1, 0);
        mainGrid.Controls.Add(cardProd,  2, 0);
        mainGrid.Controls.Add(cardQueue, 3, 0);

        // Row 1: full-width process flow
        var cardFlow = BuildProcessFlowCard();
        mainGrid.Controls.Add(cardFlow, 0, 1);
        mainGrid.SetColumnSpan(cardFlow, 4);

        // Row 2: vision(span2) | result | env
        var cardVision = BuildVisionCard();
        var cardResult = BuildResultCard();
        var cardEnv    = BuildEnvCard();
        mainGrid.Controls.Add(cardVision, 0, 2);
        mainGrid.SetColumnSpan(cardVision, 2);
        mainGrid.Controls.Add(cardResult, 2, 2);
        mainGrid.Controls.Add(cardEnv,    3, 2);

        // ── Sidebar ──
        var sidebar = BuildSidebar();

        // ── Top bar ──
        var topBar = BuildTopBar();

        // Dock order: add Fill first, then Left, Bottom, Top (processed in reverse)
        Controls.Add(mainGrid);
        Controls.Add(tabLogs);
        Controls.Add(sidebar);
        Controls.Add(topBar);

        // Orphan field assignments — all old inspection labels are superseded by
        // the new lblResult*/picResultThumb fields but must stay non-null so that
        // MainForm.cs legacy assignments don't throw NullReferenceException.
        lblLastTime      = new Label();
        lblLastVerdict   = new Label();
        picLastImage     = new PictureBox();
        lblLastId        = new Label();
        lblLastType      = new Label();
        lblLastYoloClass = new Label();
        lblLastConf      = new Label();
        lblLastPins      = new Label();
        lblLastBlur      = new Label();
        lblLastImage     = new Label();

        ResumeLayout(false);
    }

    // ══════════════════════════════════════════════════════════════
    //  TOP BAR
    // ══════════════════════════════════════════════════════════════
    private Panel BuildTopBar()
    {
        var bar = new Panel
        {
            Height    = 65,
            BackColor = DesignTokens.BgHeaderDark,
        };

        // System state chip
        lblSystemState = new Label
        {
            Text      = "● IDLE",
            ForeColor = DesignTokens.TextPrimary,
            BackColor = DesignTokens.Neutral,
            Font      = DesignTokens.FontBodyBold,
            AutoSize  = false,
            Size      = new Size(130, 28),
            Location  = new Point(16, 18),
            TextAlign = ContentAlignment.MiddleCenter,
        };

        // Cosmetic info blocks (static — no field binding needed)
        var lblMode = MakeTopInfoBlock("모드",   "자동",   168);
        var lblLine = MakeTopInfoBlock("라인 ID", "LINE1", 268);

        // Utility buttons (right-anchored)
        btnYoloHealth = MakeTopBarButton("YOLO ♥");
        btnYoloHealth.Size     = new Size(88, 36);
        btnYoloHealth.Location = new Point(ClientSize.Width - 320, 14);
        btnYoloHealth.Anchor   = AnchorStyles.Top | AnchorStyles.Right;
        btnYoloHealth.Click   += btnYoloHealth_Click;

        btnDbTest = MakeTopBarButton("DB 테스트");
        btnDbTest.Size     = new Size(88, 36);
        btnDbTest.Location = new Point(ClientSize.Width - 224, 14);
        btnDbTest.Anchor   = AnchorStyles.Top | AnchorStyles.Right;
        btnDbTest.Click   += btnDbTest_Click;

        // Clock label (right-anchored)
        var lblClock = new Label
        {
            Text      = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            Font      = DesignTokens.FontBodyBold,
            ForeColor = DesignTokens.TextOnDark,
            BackColor = Color.Transparent,
            AutoSize  = false,
            Size      = new Size(172, 20),
            Location  = new Point(ClientSize.Width - 188, 22),
            Anchor    = AnchorStyles.Top | AnchorStyles.Right,
            TextAlign = ContentAlignment.MiddleRight,
        };
        _clockTimer.Tick += (_, _) =>
        {
            if (!lblClock.IsDisposed) lblClock.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        };

        bar.Controls.AddRange(new Control[]
        {
            lblSystemState, lblMode, lblLine,
            btnYoloHealth, btnDbTest, lblClock,
        });
        return bar;
    }

    // Two-line info block for the top bar (caption in small gray, value in white bold)
    private static Panel MakeTopInfoBlock(string caption, string value, int x)
    {
        var pnl = new Panel
        {
            BackColor = Color.Transparent,
            Size      = new Size(90, 44),
            Location  = new Point(x, 10),
        };
        pnl.Controls.Add(new Label
        {
            Text      = caption,
            Font      = DesignTokens.FontLabel,
            ForeColor = Color.FromArgb(148, 163, 184),
            BackColor = Color.Transparent,
            AutoSize  = false,
            Size      = new Size(90, 16),
            Location  = new Point(0, 0),
            TextAlign = ContentAlignment.MiddleLeft,
        });
        pnl.Controls.Add(new Label
        {
            Text      = value,
            Font      = DesignTokens.FontBodyBold,
            ForeColor = Color.White,
            BackColor = Color.Transparent,
            AutoSize  = false,
            Size      = new Size(90, 22),
            Location  = new Point(0, 18),
            TextAlign = ContentAlignment.MiddleLeft,
        });
        return pnl;
    }

    // ══════════════════════════════════════════════════════════════
    //  SIDEBAR
    // ══════════════════════════════════════════════════════════════
    private static Panel BuildSidebar()
    {
        var bar = new Panel
        {
            Width     = 188,
            BackColor = DesignTokens.BgSidebar,
            Padding   = new Padding(0, 6, 0, 0),
        };

        // Right border line
        bar.Paint += (_, e) =>
        {
            using var pen = new Pen(Color.FromArgb(50, 70, 110), 1);
            e.Graphics.DrawLine(pen, bar.Width - 1, 0, bar.Width - 1, bar.Height);
        };

        var items = new[]
        {
            ("메인",          true),
            ("라인 모니터",   false),
            ("비전 검사",     false),
            ("I/O 모니터",   false),
            ("MQTT / 통신",  false),
            ("알람 / 이벤트", false),
            ("설정",          false),
        };

        int y = 8;
        foreach (var (text, active) in items)
        {
            var lbl = new Label
            {
                Text      = text,
                Font      = active ? DesignTokens.FontBodyBold : DesignTokens.FontBody,
                ForeColor = active ? Color.White : Color.FromArgb(148, 163, 184),
                BackColor = active
                    ? Color.FromArgb(59, 130, 246)
                    : Color.Transparent,
                AutoSize  = false,
                Size      = new Size(bar.Width - 1, 32),
                Location  = new Point(0, y),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding   = new Padding(14, 0, 0, 0),
            };
            bar.Controls.Add(lbl);
            y += 34;
        }

        return bar;
    }

    // ══════════════════════════════════════════════════════════════
    //  CARD: 운전 제어
    // ══════════════════════════════════════════════════════════════
    private CardPanel BuildControlCard()
    {
        var card = new CardPanel { Title = "운전 제어", Dock = DockStyle.Fill, Margin = new Padding(4) };

        btnStart     = MakeOpBtn("시 작",    DesignTokens.Ok);
        btnStop      = MakeOpBtn("일시정지", DesignTokens.Info);      // RUNNING → PAUSED + 검사 트리거
        btnResume    = MakeOpBtn("재 개",    DesignTokens.Ok);         // PAUSED  → RUNNING
        btnEmergency = MakeOpBtn("비상 정지", Color.FromArgb(176, 0, 0));
        btnReset     = MakeOpBtn("리 셋",    DesignTokens.Neutral);

        btnStart.Enabled     = true;
        btnStop.Enabled      = false;
        btnResume.Enabled    = false;
        btnReset.Enabled     = false;
        btnEmergency.Height  = 40;

        btnStart.Click     += btnStart_Click;
        btnStop.Click      += btnStop_Click;
        btnResume.Click    += btnResume_Click;
        btnEmergency.Click += btnEmergency_Click;
        btnReset.Click     += btnReset_Click;

        var tbl = MakeVStack(5);
        tbl.RowStyles[0].Height = 34;
        tbl.RowStyles[1].Height = 34;
        tbl.RowStyles[2].Height = 34;
        tbl.RowStyles[3].Height = 42;
        tbl.RowStyles[4].SizeType = SizeType.Percent;
        tbl.RowStyles[4].Height   = 100f;

        btnStart.Dock = btnStop.Dock = btnResume.Dock =
            btnEmergency.Dock = btnReset.Dock = DockStyle.Fill;

        tbl.Controls.Add(btnStart,     0, 0);
        tbl.Controls.Add(btnStop,      0, 1);
        tbl.Controls.Add(btnResume,    0, 2);
        tbl.Controls.Add(btnEmergency, 0, 3);
        tbl.Controls.Add(btnReset,     0, 4);

        card.Controls.Add(tbl);
        return card;
    }

    // ══════════════════════════════════════════════════════════════
    //  CARD: 노드 상태
    // ══════════════════════════════════════════════════════════════
    private CardPanel BuildNodeStatusCard()
    {
        var card = new CardPanel { Title = "노드 상태", Dock = DockStyle.Fill, Margin = new Padding(4) };

        // Orphan labels — kept non-null for UpdateStatusIndicator compatibility in MainForm.cs
        lblMqttStatus   = new Label();
        lblSerialStatus = new Label();
        lblRpiStatus    = new Label();
        lblYoloStatus   = new Label();
        lblDbStatus     = new Label();

        nodeMqtt   = new NodeStatusRow { NodeName = "MQTT Broker",  Dock = DockStyle.Fill };
        nodeSerial = new NodeStatusRow { NodeName = "Arduino Mega", Dock = DockStyle.Fill };
        nodeRpi    = new NodeStatusRow { NodeName = "RPi Edge",     Dock = DockStyle.Fill };
        nodeYolo   = new NodeStatusRow { NodeName = "YOLO Service", Dock = DockStyle.Fill };
        nodeDb     = new NodeStatusRow { NodeName = "MySQL DB",     Dock = DockStyle.Fill };

        var tbl = MakeVStack(5);
        for (int i = 0; i < 5; i++)
        {
            tbl.RowStyles[i].SizeType = SizeType.Percent;
            tbl.RowStyles[i].Height   = 20f;
        }

        tbl.Controls.Add(nodeMqtt,   0, 0);
        tbl.Controls.Add(nodeSerial, 0, 1);
        tbl.Controls.Add(nodeRpi,    0, 2);
        tbl.Controls.Add(nodeYolo,   0, 3);
        tbl.Controls.Add(nodeDb,     0, 4);

        card.Controls.Add(tbl);
        return card;
    }

    // ══════════════════════════════════════════════════════════════
    //  CARD: 생산 통계
    // ══════════════════════════════════════════════════════════════
    private CardPanel BuildProductionCard()
    {
        var card = new CardPanel { Title = "생산 / 검사 정보", Dock = DockStyle.Fill, Margin = new Padding(4) };

        lblTotal     = MakeStatLabel("전체: 0",    DesignTokens.TextPrimary);
        lblPass      = MakeStatLabel("PASS: 0",    DesignTokens.Pass);
        lblDefect    = MakeStatLabel("DEFECT: 0",  DesignTokens.Defect);
        lblHold      = MakeStatLabel("HOLD: 0",    DesignTokens.Hold);
        lblPassRate  = MakeStatLabel("양품률: —",  DesignTokens.TextPrimary);
        lblQueueDepth= new Label();   // orphan — not added to UI

        lblPass.Font = lblDefect.Font = lblHold.Font = DesignTokens.FontMetricSmall;

        var tbl = new TableLayoutPanel
        {
            Dock        = DockStyle.Fill,
            ColumnCount = 2,
            RowCount    = 3,
            Padding     = new Padding(0, 2, 0, 0),
        };
        tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        for (int i = 0; i < 3; i++)
            tbl.RowStyles.Add(new RowStyle(SizeType.Percent, 33.3f));

        lblTotal.Dock = lblPass.Dock =
            lblDefect.Dock = lblHold.Dock = lblPassRate.Dock = DockStyle.Fill;

        tbl.Controls.Add(lblTotal,    0, 0);
        tbl.Controls.Add(lblPassRate, 1, 0);
        tbl.Controls.Add(lblPass,     0, 1);
        tbl.Controls.Add(lblDefect,   1, 1);
        tbl.Controls.Add(lblHold,     0, 2);

        card.Controls.Add(tbl);
        return card;
    }

    // ══════════════════════════════════════════════════════════════
    //  CARD: 처리 큐
    // ══════════════════════════════════════════════════════════════
    private CardPanel BuildQueueCard()
    {
        var card = new CardPanel { Title = "관리", Dock = DockStyle.Fill, Margin = new Padding(4) };

        btnClearStats  = MakeSmallBtn("통계 초기화", DesignTokens.Neutral);
        btnAuditVerify = MakeSmallBtn("감사 검증",   Color.FromArgb(90, 50, 140));

        btnClearStats.Click  += btnClearStats_Click;
        btnAuditVerify.Click += btnAuditVerify_Click;

        var btnGrid = new TableLayoutPanel
        {
            Dock        = DockStyle.Fill,
            ColumnCount = 2,
            RowCount    = 1,
            Padding     = new Padding(0, 4, 0, 0),
        };
        btnGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        btnGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        btnGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        btnClearStats.Dock = btnAuditVerify.Dock = DockStyle.Fill;

        btnGrid.Controls.Add(btnClearStats,  0, 0);
        btnGrid.Controls.Add(btnAuditVerify, 1, 0);

        card.Controls.Add(btnGrid);
        return card;
    }

    // ══════════════════════════════════════════════════════════════
    //  CARD: 공정 흐름
    // ══════════════════════════════════════════════════════════════
    private CardPanel BuildProcessFlowCard()
    {
        var card = new CardPanel { Title = "공정 흐름 / 장비 모니터링", Dock = DockStyle.Fill, Margin = new Padding(4) };

        _processFlow = new ProcessFlowView { Dock = DockStyle.Fill };
        _processFlow.Steps = new List<ProcessFlowView.Step>
        {
            new("투입 컨베이어",        "속도 120 mm/s", "정지",    DesignTokens.Neutral),
            new("CAM1 상부 검사",       "FPS: 실시간",   "대기",    DesignTokens.Neutral),
            new("판정 엔진",            "YOLO + OpenCV", "대기",    DesignTokens.Neutral),
            new("분류 서보",            "위치 45.0°",    "대기",    DesignTokens.Neutral),
            new("PASS / DEFECT / HOLD", "",              "—",       DesignTokens.Neutral),
        };

        card.Controls.Add(_processFlow);
        return card;
    }

    // ══════════════════════════════════════════════════════════════
    //  CARD: 비전 검사 모니터
    // ══════════════════════════════════════════════════════════════
    private CardPanel BuildVisionCard()
    {
        var card = new CardPanel { Title = "비전 검사 모니터", Dock = DockStyle.Fill, Margin = new Padding(4) };

        // ── Header strip (title + clock) ────────────────────────────
        lblCam1Time = new Label
        {
            Text      = "시각: —",
            Dock      = DockStyle.Right,
            Width     = 110,
            Font      = DesignTokens.FontLabel,
            ForeColor = DesignTokens.TextSecondary,
            BackColor = Color.Transparent,
            TextAlign = ContentAlignment.MiddleRight,
            Padding   = new Padding(0, 0, 4, 0),
        };
        _clockTimer.Tick += (_, _) =>
        {
            if (!lblCam1Time.IsDisposed) lblCam1Time.Text = $"시각: {DateTime.Now:HH:mm:ss}";
        };

        var lblCam1Header = new Label
        {
            Text      = "CAM1 — 검사 (라이브)",
            Dock      = DockStyle.Fill,
            Font      = DesignTokens.FontLabel,
            ForeColor = DesignTokens.TextSecondary,
            BackColor = Color.Transparent,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding   = new Padding(4, 0, 0, 0),
        };

        var pnlHeader = new Panel { Dock = DockStyle.Top, Height = 22, BackColor = Color.Transparent };
        pnlHeader.Controls.Add(lblCam1Header);  // Fill — added first (behind)
        pnlHeader.Controls.Add(lblCam1Time);    // Right — added second (in front)

        // ── Main vision area ─────────────────────────────────────────
        var pnlVisionMain = new Panel { Dock = DockStyle.Fill, BackColor = Color.Black };

        // CAM1 fills entire area — added FIRST → lower z-order (behind PiP)
        webCam1Live = new WebView2WinForms.WebView2 { Dock = DockStyle.Fill };
        pnlVisionMain.Controls.Add(webCam1Live);

        // CAM2 PiP — top-right overlay — added SECOND → higher z-order (in front)
        webCam2Live = new WebView2WinForms.WebView2 { Dock = DockStyle.Fill };
        var lblCam2Pip = new Label
        {
            Text      = "CAM2",
            Dock      = DockStyle.Top,
            Height    = 18,
            Font      = DesignTokens.FontLabel,
            ForeColor = Color.White,
            BackColor = Color.FromArgb(30, 30, 30),
            TextAlign = ContentAlignment.MiddleCenter,
        };

        var pnlCam2Pip = new Panel { Size = new Size(224, 168), BackColor = Color.Black };
        pnlCam2Pip.Controls.Add(webCam2Live);  // Fill — added first
        pnlCam2Pip.Controls.Add(lblCam2Pip);   // Top — added second (on top within pip)

        // Maintain top-right position on resize
        pnlVisionMain.Resize += (_, _) =>
        {
            if (pnlVisionMain.Width > pnlCam2Pip.Width + 16)
                pnlCam2Pip.Location = new Point(pnlVisionMain.Width - pnlCam2Pip.Width - 8, 8);
        };

        pnlVisionMain.Controls.Add(pnlCam2Pip);
        pnlCam2Pip.BringToFront();  // explicitly on top of webCam1Live HWND

        // Add to card: Fill first, then Top (WinForms dock processes in reverse add-order)
        card.Controls.Add(pnlVisionMain);  // Fill — added first
        card.Controls.Add(pnlHeader);      // Top — added second (claimed first in layout)
        return card;
    }

    // ══════════════════════════════════════════════════════════════
    //  CARD: 환경 센서
    // ══════════════════════════════════════════════════════════════
    private CardPanel BuildEnvCard()
    {
        var card = new CardPanel { Title = "환경 센서", Dock = DockStyle.Fill, Margin = new Padding(4) };

        // Orphan labels — kept non-null for UpdateSensorUI compatibility in MainForm.cs
        lblTemp     = new Label();
        lblHumidity = new Label();
        lblGas      = new Label();

        metricTemp = new MetricCard { Caption = "온도",     Unit = "°C",  Dock = DockStyle.Fill };
        metricHum  = new MetricCard { Caption = "습도",     Unit = "%",   Dock = DockStyle.Fill };
        metricGas  = new MetricCard { Caption = "가스 농도", Unit = "ppm", Dock = DockStyle.Fill };

        var tbl = MakeVStack(3);
        for (int i = 0; i < 3; i++)
        {
            tbl.RowStyles[i].SizeType = SizeType.Percent;
            tbl.RowStyles[i].Height   = 33.3f;
        }

        tbl.Controls.Add(metricTemp, 0, 0);
        tbl.Controls.Add(metricHum,  0, 1);
        tbl.Controls.Add(metricGas,  0, 2);

        card.Controls.Add(tbl);
        return card;
    }

    // ══════════════════════════════════════════════════════════════
    //  CARD: 최근 검사 결과 (verdict badge + detail table)
    // ══════════════════════════════════════════════════════════════
    private CardPanel BuildResultCard()
    {
        var card = new CardPanel { Title = "최근 검사 결과", Dock = DockStyle.Fill, Margin = new Padding(4) };

        // ── 1. 큰 판정 뱃지 ──
        pnlVerdictBadge = new Panel
        {
            Dock   = DockStyle.Top,
            Height = 80,
            Margin = new Padding(0, 0, 0, 10),
        };
        lblResultBigVerdict = new Label
        {
            Text      = "—",
            Dock      = DockStyle.Fill,
            Font      = new Font(DesignTokens.FontFamily, 32f, FontStyle.Bold),
            ForeColor = DesignTokens.TextSecondary,
            TextAlign = ContentAlignment.MiddleCenter,
            BackColor = Color.FromArgb(243, 244, 246),
        };
        pnlVerdictBadge.Controls.Add(lblResultBigVerdict);

        // ── 2. 검사 캡처 썸네일 ──
        picResultThumb = new PictureBox
        {
            Dock      = DockStyle.Top,
            Height    = 110,
            SizeMode  = PictureBoxSizeMode.Zoom,
            BackColor = Color.FromArgb(243, 244, 246),
            Margin    = new Padding(0, 0, 0, 10),
        };

        // ── 3. 디테일 표 ──
        var detail = new TableLayoutPanel
        {
            Dock        = DockStyle.Fill,
            ColumnCount = 2,
            RowCount    = 6,
            BackColor   = Color.Transparent,
            Padding     = new Padding(0, 4, 0, 0),
        };
        detail.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 60f));
        detail.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        for (int i = 0; i < 6; i++)
            detail.RowStyles.Add(new RowStyle(SizeType.Absolute, 22f));

        AddDetailRow(detail, 0, "ID",     out lblResultId);
        AddDetailRow(detail, 1, "종류",   out lblResultType);
        AddDetailRow(detail, 2, "YOLO",   out lblResultYolo);
        AddDetailRow(detail, 3, "신뢰도", out lblResultConf);
        AddDetailRow(detail, 4, "핀 수",  out lblResultPins);
        AddDetailRow(detail, 5, "선명도", out lblResultBlur);

        // 추가 순서 중요: Fill → 두 번째 Top → 첫 번째 Top (역순으로 Top이 쌓임)
        card.Controls.Add(detail);           // Fill
        card.Controls.Add(picResultThumb);   // Top, 두 번째
        card.Controls.Add(pnlVerdictBadge);  // Top, 가장 위
        return card;
    }

    private static void AddDetailRow(TableLayoutPanel host, int row, string caption,
        out Label valueLabel)
    {
        host.Controls.Add(new Label
        {
            Text      = caption,
            Font      = DesignTokens.FontLabel,
            ForeColor = DesignTokens.TextSecondary,
            TextAlign = ContentAlignment.MiddleLeft,
            Dock      = DockStyle.Fill,
            AutoSize  = false,
        }, 0, row);

        valueLabel = new Label
        {
            Text      = "—",
            Font      = DesignTokens.FontBodyBold,
            ForeColor = DesignTokens.TextPrimary,
            TextAlign = ContentAlignment.MiddleLeft,
            Dock      = DockStyle.Fill,
            AutoSize  = false,
        };
        host.Controls.Add(valueLabel, 1, row);
    }

    // ══════════════════════════════════════════════════════════════
    //  STATIC HELPERS
    // ══════════════════════════════════════════════════════════════
    private static Button MakeTopBarButton(string text) => new()
    {
        Text      = text,
        Font      = DesignTokens.FontBodyBold,
        BackColor = Color.FromArgb(55, 65, 81),
        ForeColor = Color.White,
        FlatStyle = FlatStyle.Flat,
        Cursor    = Cursors.Hand,
    };

    private static Button MakeOpBtn(string text, Color back) => new()
    {
        Text      = text,
        BackColor = back,
        ForeColor = Color.White,
        FlatStyle = FlatStyle.Flat,
        Font      = DesignTokens.FontBodyBold,
        Cursor    = Cursors.Hand,
        Margin    = new Padding(0, 0, 0, 2),
    };

    private static Button MakeSmallBtn(string text, Color back) => new()
    {
        Text      = text,
        BackColor = back,
        ForeColor = Color.White,
        FlatStyle = FlatStyle.Flat,
        Font      = DesignTokens.FontBody,
        Cursor    = Cursors.Hand,
        Margin    = new Padding(2),
    };

    private static Label MakeStatLabel(string text, Color fore) => new()
    {
        Text      = text,
        Font      = DesignTokens.FontBody,
        ForeColor = fore,
        AutoSize  = false,
        TextAlign = ContentAlignment.MiddleLeft,
        Padding   = new Padding(4, 0, 0, 0),
    };

    // 1-column N-row TableLayoutPanel helper
    private static TableLayoutPanel MakeVStack(int rows)
    {
        var tbl = new TableLayoutPanel
        {
            Dock        = DockStyle.Fill,
            ColumnCount = 1,
            RowCount    = rows,
            Padding     = new Padding(0, 2, 0, 0),
        };
        tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        for (int i = 0; i < rows; i++)
            tbl.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        return tbl;
    }

    private static RichTextBox MakeRtb() => new()
    {
        Dock        = DockStyle.Fill,
        ReadOnly    = true,
        BackColor   = Color.FromArgb(20, 20, 20),
        ForeColor   = Color.LightGray,
        Font        = DesignTokens.FontMono,
        ScrollBars  = RichTextBoxScrollBars.Vertical,
        WordWrap    = false,
    };
}
