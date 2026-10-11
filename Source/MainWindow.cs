using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace DL1Assistant
{
    internal sealed partial class MainForm : Form
    {
        private readonly string _rootDir;
        private readonly Dictionary<int, BountyCatalogItem> _bountyCatalog;
        private readonly TaskCatalogRoot _taskCatalog;
        private readonly List<WeaponCatalogItem> _weapons;
        private readonly List<CollectibleCategory> _collectibleCategories;
        private readonly Dictionary<string, BlueprintCatalogItem> _blueprintCatalog;
        private readonly Dictionary<string, string> _itemTranslations;
        private List<BlueprintRuntimeRecord> _blueprintRuntime = new List<BlueprintRuntimeRecord>();
        private readonly HashSet<int> _hellraidBountyIds;
        private readonly GameClient _game;

        private readonly Dictionary<string, RoundedButton> _navButtons = new Dictionary<string, RoundedButton>();
        private readonly Dictionary<string, Panel> _pages = new Dictionary<string, Panel>();
        private Panel _contentHost;
        private Label _connectionDot;
        private Label _connectionText;
        private RoundedButton _connectButton;
        private TextBox _logBox;

        private DataGridView _taskGrid;
        private FlowLayoutPanel _taskTabs;
        private string _taskMode = "悬赏";
        private Label _detailName;
        private Label _detailMeta;
        private Label _detailReward;
        private TextBox _objectives;
        private RoundedButton _acceptButton;
        private RoundedButton _refreshButton;
        private RoundedButton _completeButton;
        private RoundedButton _completeAllButton;
        private RoundedButton _bountyProbeAButton;
        private RoundedButton _bountyProbeBButton;
        private TaskCatalogItem _selectedQuestItem;
        private ComboBox _probeKind;
        private Label _probeStatus;
        private int _selectedBountyId = -1;
        private IntPtr _selectedBountyPtr = IntPtr.Zero;
        private int _selectedBountyState = -1;
        private bool _refreshing;

        private ToggleSwitch _stealthToggle;
        private NumberPicker _runPicker;
        private NumberPicker _jumpPicker;
        private ComboBox _weaponCategory;
        private ComboBox _weaponPicker;
        private Label _weaponDetail;
        private Label _runHitStatus;

        private DataGridView _collectibleGrid;
        private Label _collectibleName;
        private Label _collectibleMeta;
        private TextBox _collectibleDetail;
        private RoundedButton _collectibleOneButton;
        private RoundedButton _collectibleAllButton;
        private RoundedButton _collectibleRefreshButton;
        private RoundedButton _collectibleAllSupportedButton;
        private RoundedButton _collectibleSnapshotButton;
        private HashSet<int> _collectedIds = new HashSet<int>();
        private List<string> _blueprintIds = new List<string>();
        private HashSet<int> _v011SampleCollectedIds;
        private string _v011SampleKind;
        private readonly Dictionary<string, List<CollectibleItem>> _v011Observed =
            new Dictionary<string, List<CollectibleItem>>(StringComparer.OrdinalIgnoreCase);

        private DataGridView _hellraidGrid;
        private Label _hellraidStatus;
        private RoundedButton _skeletonButton;
        private bool _busy;
        private bool _closing;

        internal MainForm(string rootDir)
        {
            _rootDir = rootDir;
            _bountyCatalog = LoadBounties(Path.Combine(rootDir, "Data", "Bounties.json"));
            _taskCatalog = LoadTasks(Path.Combine(rootDir, "Data", "Tasks.json"));
            _weapons = LoadWeapons(Path.Combine(rootDir, "Data", "LegendaryWeapons.json"));
            _collectibleCategories = LoadCollectibles(Path.Combine(rootDir, "Data", "Collectibles.json"));
            _blueprintCatalog = LoadBlueprintCatalog(Path.Combine(rootDir, "Data", "BlueprintCatalog.json"));
            _itemTranslations = LoadItemTranslations(Path.Combine(rootDir, "Data", "LocalizedItems.json"));
            _hellraidBountyIds = LoadHellraidIds(Path.Combine(rootDir, "Data", "HellraidBounties.json"));
            _game = new GameClient(_bountyCatalog);
            LoadObservedCollectiblesV011();
            BuildUi();
            Shown += delegate
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    if (!_game.IsConnected) ConnectSafe(false);
                });
            };

            Log("消逝的光芒任务 / Hellraid 助手 v0.1.2 已启动。");
            Log("悬赏 v0.1.2：根据实测 Progress+0x70 直接清零当前目标，再追加到 BountyManager+0x6F0 原生评估队列，由游戏线程执行 Evaluate/状态收束；不再从远程线程调用 Progress/Evaluate，也不强制 event=5/6。");
            Log("奔跑速度 v0.1.2：CE WalkSpeed 与 IMIG SprintSpeed 双 Hook 保留；新增真实冲刺参数 RDX+0x08 的捕获/读写，并对速度上限、加速度及移动乘数补充 750ms 非UI低频维护。命中不代表实际移速增加，按实测位移确认。 ");
            Log("跳高：当前 1.55 Jump AOB 只在首次安装时校验；Hook 存续期间可直接 x2→x3→x10 修改倍率，不再要求先回 x1。");
            Log("隐身：已接当前 1.55 XXXeLxxx CT 的 Stealth [day/night] 消费点 gamedll+0x6C4D22；不再写错误 player+0x10B4。");
            Log("武器 v0.1.2：沿用已实测能入包的 NameId AddItem；校验返回实例后尝试将品质字节置为橙色(4)，也支持在新背包页手动修改数量、品质和基础伤害。 ");
            Log("悬赏：新增选中悬赏 A/B。A 后只正常完成 1 个最小目标单位，再采 B，用对象图差异定位 authoritative progress，而不是继续写表面 Remaining。");
            Log("收集品 v0.1.2：已从你提供的 Data0.pak/DataCn.pak 提取真实物品名称与 255 种 CraftPlan 定义；特殊收集品及非同容器蓝图的未验证持久进度显示 ?，不会误判为 ×。 ");
            Log("目标 gamedll SHA-256：" + GameClient.ExpectedHash);
        }

        private static Dictionary<int, BountyCatalogItem> LoadBounties(string path)
        {
            JavaScriptSerializer js = new JavaScriptSerializer(); js.MaxJsonLength = int.MaxValue;
            List<BountyCatalogItem> list = js.Deserialize<List<BountyCatalogItem>>(File.ReadAllText(path, Encoding.UTF8));
            Dictionary<int, BountyCatalogItem> result = new Dictionary<int, BountyCatalogItem>();
            if (list != null) foreach (BountyCatalogItem item in list) result[item.id] = item;
            return result;
        }

        private static TaskCatalogRoot LoadTasks(string path)
        {
            JavaScriptSerializer js = new JavaScriptSerializer(); js.MaxJsonLength = int.MaxValue;
            TaskCatalogRoot root = js.Deserialize<TaskCatalogRoot>(File.ReadAllText(path, Encoding.UTF8));
            if (root == null) root = new TaskCatalogRoot();
            if (root.categories == null) root.categories = new Dictionary<string, List<TaskCatalogItem>>();
            return root;
        }

        private static List<WeaponCatalogItem> LoadWeapons(string path)
        {
            if (!File.Exists(path)) return new List<WeaponCatalogItem>();
            JavaScriptSerializer js = new JavaScriptSerializer(); js.MaxJsonLength = int.MaxValue;
            List<WeaponCatalogItem> list = js.Deserialize<List<WeaponCatalogItem>>(File.ReadAllText(path, Encoding.UTF8));
            return list ?? new List<WeaponCatalogItem>();
        }

        private static List<CollectibleCategory> LoadCollectibles(string path)
        {
            if (!File.Exists(path)) return new List<CollectibleCategory>();
            JavaScriptSerializer js = new JavaScriptSerializer(); js.MaxJsonLength = int.MaxValue;
            CollectibleCatalogRoot root = js.Deserialize<CollectibleCatalogRoot>(File.ReadAllText(path, Encoding.UTF8));
            return root == null || root.categories == null ? new List<CollectibleCategory>() : root.categories;
        }

        private static Dictionary<string, string> LoadItemTranslations(string path)
        {
            if (!File.Exists(path)) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            JavaScriptSerializer js = new JavaScriptSerializer(); js.MaxJsonLength = int.MaxValue;
            return js.Deserialize<Dictionary<string, string>>(File.ReadAllText(path, Encoding.UTF8)) ??
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        private string ItemChineseV012(string id)
        {
            if (String.IsNullOrEmpty(id)) return "";
            string text;
            return _itemTranslations.TryGetValue(id, out text) && !String.IsNullOrWhiteSpace(text) ? text : id;
        }

        private static Dictionary<string, BlueprintCatalogItem> LoadBlueprintCatalog(string path)
        {
            Dictionary<string, BlueprintCatalogItem> result = new Dictionary<string, BlueprintCatalogItem>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(path)) return result;
            JavaScriptSerializer js = new JavaScriptSerializer(); js.MaxJsonLength = int.MaxValue;
            List<BlueprintCatalogItem> list = js.Deserialize<List<BlueprintCatalogItem>>(File.ReadAllText(path, Encoding.UTF8));
            if (list != null) foreach (BlueprintCatalogItem item in list)
                if (!String.IsNullOrEmpty(item.internalId)) result[item.internalId] = item;
            return result;
        }

        private string BlueprintChineseV012(string id)
        {
            BlueprintCatalogItem item;
            return _blueprintCatalog.TryGetValue(id, out item) && !String.IsNullOrWhiteSpace(item.name)
                ? item.name : id;
        }

        private static HashSet<int> LoadHellraidIds(string path)
        {
            HashSet<int> ids = new HashSet<int>();
            if (!File.Exists(path)) return ids;
            try
            {
                JavaScriptSerializer js = new JavaScriptSerializer(); js.MaxJsonLength = int.MaxValue;
                List<BountyCatalogItem> list = js.Deserialize<List<BountyCatalogItem>>(File.ReadAllText(path, Encoding.UTF8));
                if (list != null) foreach (BountyCatalogItem item in list) ids.Add(item.id);
            }
            catch { }
            return ids;
        }

        private void BuildUi()
        {
            Text = "消逝的光芒任务 / Hellraid 助手 v0.1.2";
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(1280, 820);
            MinimumSize = new Size(980, 660);
            BackColor = Theme.Background;
            ForeColor = Theme.Text;
            Font = Theme.Normal;
            AutoScaleMode = AutoScaleMode.Dpi;
            DoubleBuffered = true;
            FormClosing += OnFormClosing;

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill; root.Margin = Padding.Empty; root.Padding = Padding.Empty;
            root.ColumnCount = 2; root.RowCount = 1; root.BackColor = Theme.Background;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 178F));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            Controls.Add(root);
            root.Controls.Add(BuildSidebar(), 0, 0);
            root.Controls.Add(BuildMainArea(), 1, 0);
            ShowPage("任务");
        }

        private Control BuildSidebar()
        {
            TableLayoutPanel side = new TableLayoutPanel();
            side.Dock = DockStyle.Fill; side.Margin = Padding.Empty; side.Padding = new Padding(14, 18, 14, 14);
            side.BackColor = Theme.Sidebar; side.ColumnCount = 1; side.RowCount = 3;
            side.RowStyles.Add(new RowStyle(SizeType.Absolute, 82F));
            side.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            side.RowStyles.Add(new RowStyle(SizeType.Absolute, 74F));

            Panel brand = new Panel(); brand.Dock = DockStyle.Fill; brand.BackColor = Theme.Sidebar;
            Label title = Theme.Label("DL1 助手", 15.5F, FontStyle.Bold, Theme.Text); title.Location = new Point(8, 7);
            Label sub = Theme.Label("任务 · Hellraid · v0.1.2", 8F, FontStyle.Regular, Theme.Muted); sub.Location = new Point(9, 42);
            brand.Controls.Add(title); brand.Controls.Add(sub); side.Controls.Add(brand, 0, 0);

            FlowLayoutPanel nav = new FlowLayoutPanel();
            nav.Dock = DockStyle.Fill; nav.FlowDirection = FlowDirection.TopDown; nav.WrapContents = false; nav.AutoScroll = true;
            nav.BackColor = Theme.Sidebar; nav.Margin = Padding.Empty;
            foreach (string name in new string[] { "任务", "辅助", "背包 / 库存", "收集品", "Hellraid", "日志", "设置" })
            {
                RoundedButton b = new RoundedButton(); b.Text = name; b.Width = 146; b.Height = 40; b.Margin = new Padding(2, 4, 2, 4);
                b.TextAlign = ContentAlignment.MiddleLeft; b.NormalColor = Theme.Sidebar; b.HoverColor = Theme.Card2; b.ForeColor = Theme.Text; b.SurfaceColor = Theme.Sidebar;
                string n = name; b.Click += delegate { ShowPage(n); };
                _navButtons[name] = b; nav.Controls.Add(b);
            }
            side.Controls.Add(nav, 0, 1);

            RoundedPanel footer = new RoundedPanel(); footer.Dock = DockStyle.Fill; footer.Margin = Padding.Empty; footer.Padding = new Padding(10, 9, 10, 9); footer.BackColor = Theme.Card; footer.BorderColor = Theme.Line; footer.SurfaceColor = Theme.Sidebar;
            Label f = Theme.Label("Steam 1.55\r\nSHA 锁定写入", 7.7F, FontStyle.Regular, Theme.Muted); f.Dock = DockStyle.Fill; footer.Controls.Add(f); side.Controls.Add(footer, 0, 2);
            return side;
        }

        private Control BuildMainArea()
        {
            TableLayoutPanel main = new TableLayoutPanel(); main.Dock = DockStyle.Fill; main.Margin = Padding.Empty; main.Padding = new Padding(18, 12, 18, 16); main.BackColor = Theme.Background; main.ColumnCount = 1; main.RowCount = 2;
            main.RowStyles.Add(new RowStyle(SizeType.Absolute, 52F)); main.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            main.Controls.Add(BuildTopBar(), 0, 0);

            _contentHost = new Panel(); _contentHost.Dock = DockStyle.Fill; _contentHost.Margin = Padding.Empty; _contentHost.BackColor = Theme.Background;
            _pages["任务"] = BuildTaskPage();
            _pages["辅助"] = BuildAssistPage();
            _pages["背包 / 库存"] = BuildInventoryPageV012();
            _pages["收集品"] = BuildCollectiblesPage();
            _pages["Hellraid"] = BuildHellraidPage();
            _pages["日志"] = BuildLogPage();
            _pages["设置"] = BuildSettingsPage();
            foreach (Panel page in _pages.Values) { page.Dock = DockStyle.Fill; page.Visible = false; _contentHost.Controls.Add(page); }
            main.Controls.Add(_contentHost, 0, 1);
            return main;
        }

        private Control BuildTopBar()
        {
            TableLayoutPanel bar = new TableLayoutPanel(); bar.Dock = DockStyle.Fill; bar.Margin = Padding.Empty; bar.Padding = Padding.Empty; bar.ColumnCount = 4; bar.RowCount = 1; bar.BackColor = Theme.Background;
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 18F));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150F));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 118F));

            Label version = Theme.Label("Dying Light · 当前构建 1.55", 8.2F, FontStyle.Regular, Theme.Muted); version.Dock = DockStyle.Fill; version.TextAlign = ContentAlignment.MiddleLeft; bar.Controls.Add(version, 0, 0);
            _connectionDot = Theme.Label("●", 10F, FontStyle.Bold, Theme.Bad); _connectionDot.Dock = DockStyle.Fill; _connectionDot.TextAlign = ContentAlignment.MiddleCenter; bar.Controls.Add(_connectionDot, 1, 0);
            _connectionText = Theme.Label("未连接", 8.5F, FontStyle.Bold, Theme.Muted); _connectionText.Dock = DockStyle.Fill; _connectionText.TextAlign = ContentAlignment.MiddleLeft; bar.Controls.Add(_connectionText, 2, 0);
            _connectButton = ActionButton("连接游戏", 102, true); _connectButton.Dock = DockStyle.Fill; _connectButton.Margin = new Padding(8, 8, 0, 8); _connectButton.Click += delegate { ConnectSafe(true); }; _connectButton.Cursor = Cursors.Hand; bar.Controls.Add(_connectButton, 3, 0);
            ToolTip tip = new ToolTip(); tip.SetToolTip(_connectButton, "重新扫描 DyingLightGame.exe 并连接当前存档。启动时会自动连接一次。");
            return bar;
        }

        private Panel BuildTaskPage()
        {
            Panel page = NewPage();
            TableLayoutPanel layout = new TableLayoutPanel(); layout.Dock = DockStyle.Fill; layout.Margin = Padding.Empty; layout.ColumnCount = 1; layout.RowCount = 4; layout.BackColor = Theme.Background;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64F)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48F)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44F)); page.Controls.Add(layout);
            layout.Controls.Add(PageTitle("任务", "悬赏完成改为写真实 Progress remaining + BountyManager 原生游戏线程评估队列；不再跨线程强调任务函数"), 0, 0);

            _taskTabs = new FlowLayoutPanel(); _taskTabs.Dock = DockStyle.Fill; _taskTabs.WrapContents = false; _taskTabs.BackColor = Theme.Background; _taskTabs.Margin = Padding.Empty;
            foreach (string mode in new string[] { "悬赏", "主线", "支线", "可重复任务", "挑战" })
            {
                RoundedButton b = ActionButton(mode, mode == "可重复任务" ? 116 : 92, false); b.Height = 34; b.Tag = mode; b.Margin = new Padding(0, 5, 8, 5); string m = mode; b.Click += delegate { SetTaskMode(m); }; _taskTabs.Controls.Add(b);
            }
            layout.Controls.Add(_taskTabs, 0, 1);

            TableLayoutPanel body = new TableLayoutPanel(); body.Dock = DockStyle.Fill; body.Margin = Padding.Empty; body.ColumnCount = 2; body.RowCount = 1; body.BackColor = Theme.Background; body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60F)); body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40F));
            _taskGrid = new DataGridView(); ConfigureBaseGrid(_taskGrid); _taskGrid.SelectionChanged += delegate { UpdateTaskDetail(); }; body.Controls.Add(_taskGrid, 0, 0); body.Controls.Add(BuildTaskDetailCard(), 1, 0); layout.Controls.Add(body, 0, 2);

            RoundedPanel warning = new RoundedPanel(); warning.Dock = DockStyle.Fill; warning.Margin = new Padding(0, 8, 0, 0); warning.Padding = new Padding(12, 7, 12, 7); warning.BackColor = Color.FromArgb(34, 28, 22); warning.BorderColor = Color.FromArgb(104, 80, 45);
            Label w = Theme.Label("悬赏完成：目标字段来自真实 Progress+0x70；清零后加入 BountyManager+0x6F0 原生评估队列，由游戏线程收束状态。普通 Quest 在此存档已全部完成，保留目录。", 8F, FontStyle.Regular, Theme.Warning); w.Dock = DockStyle.Fill; warning.Controls.Add(w); layout.Controls.Add(warning, 0, 3);
            SetTaskMode("悬赏");
            return page;
        }

        private Control BuildTaskDetailCard()
        {
            RoundedPanel card = new RoundedPanel(); card.Dock = DockStyle.Fill; card.Margin = new Padding(8, 0, 0, 0); card.Padding = new Padding(10, 10, 10, 10); card.BackColor = Theme.Card; card.BorderColor = Theme.Line;
            TableLayoutPanel grid = new TableLayoutPanel(); grid.Dock = DockStyle.Fill; grid.Margin = Padding.Empty; grid.ColumnCount = 1; grid.RowCount = 7; grid.BackColor = Theme.Card;
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F)); grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 24F)); grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 42F)); grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100F)); grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 46F)); grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 44F)); grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 42F)); card.Controls.Add(grid);
            _detailName = Theme.Label("请选择任务", 11F, FontStyle.Bold, Theme.Text); _detailName.Dock = DockStyle.Fill; _detailName.AutoEllipsis = true; grid.Controls.Add(_detailName, 0, 0);
            _detailMeta = Theme.Label("", 8F, FontStyle.Regular, Theme.Muted); _detailMeta.Dock = DockStyle.Fill; grid.Controls.Add(_detailMeta, 0, 1);
            _detailReward = Theme.Label("", 8F, FontStyle.Regular, Theme.Muted); _detailReward.Dock = DockStyle.Fill; _detailReward.AutoEllipsis = true; grid.Controls.Add(_detailReward, 0, 2);
            _objectives = new TextBox(); _objectives.Dock = DockStyle.Fill; _objectives.Multiline = true; _objectives.ReadOnly = true; _objectives.ScrollBars = ScrollBars.Vertical; _objectives.BorderStyle = BorderStyle.None; _objectives.BackColor = Theme.Card; _objectives.ForeColor = Theme.Text; _objectives.Font = new Font("Microsoft YaHei UI", 8.4F); grid.Controls.Add(_objectives, 0, 3);

            TableLayoutPanel actions = new TableLayoutPanel(); actions.Dock = DockStyle.Fill; actions.Margin = Padding.Empty; actions.ColumnCount = 2; actions.RowCount = 1; actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F)); actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            _acceptButton = ActionButton("接取选中", 110, true); _acceptButton.SurfaceColor = Theme.Card; _acceptButton.Dock = DockStyle.Fill; _acceptButton.Margin = new Padding(0, 5, 5, 5); _acceptButton.Click += delegate { PrimaryBountyActionSafe(); };
            _refreshButton = ActionButton("刷新", 90, false); _refreshButton.SurfaceColor = Theme.Card; _refreshButton.Dock = DockStyle.Fill; _refreshButton.Margin = new Padding(5, 5, 0, 5); _refreshButton.Click += delegate { RefreshTaskMode(); };
            actions.Controls.Add(_acceptButton, 0, 0); actions.Controls.Add(_refreshButton, 1, 0); grid.Controls.Add(actions, 0, 4);

            TableLayoutPanel disabled = new TableLayoutPanel(); disabled.Dock = DockStyle.Fill; disabled.Margin = Padding.Empty; disabled.ColumnCount = 2; disabled.RowCount = 1; disabled.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F)); disabled.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            _completeButton = ActionButton("完成选中", 110, true); _completeButton.SurfaceColor = Theme.Card; _completeButton.Dock = DockStyle.Fill; _completeButton.Margin = new Padding(0, 4, 5, 4); _completeButton.Enabled = false; _completeButton.Click += delegate { SecondaryTaskActionSafe(false); };
            _completeAllButton = ActionButton("完成全部", 110, false); _completeAllButton.SurfaceColor = Theme.Card; _completeAllButton.Dock = DockStyle.Fill; _completeAllButton.Margin = new Padding(5, 4, 0, 4); _completeAllButton.Enabled = true; _completeAllButton.Click += delegate { SecondaryTaskActionSafe(true); };
            disabled.Controls.Add(_completeButton, 0, 0); disabled.Controls.Add(_completeAllButton, 1, 0); grid.Controls.Add(disabled, 0, 5);

            TableLayoutPanel probes = new TableLayoutPanel(); probes.Dock = DockStyle.Fill; probes.Margin = Padding.Empty; probes.ColumnCount = 2; probes.RowCount = 1; probes.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F)); probes.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            _bountyProbeAButton = ActionButton("采样 A", 110, false); _bountyProbeAButton.SurfaceColor = Theme.Card; _bountyProbeAButton.Dock = DockStyle.Fill; _bountyProbeAButton.Margin = new Padding(0, 3, 5, 3); _bountyProbeAButton.Click += delegate { ExportSelectedBountyProbeSafe(false); };
            _bountyProbeBButton = ActionButton("采样 B", 110, false); _bountyProbeBButton.SurfaceColor = Theme.Card; _bountyProbeBButton.Dock = DockStyle.Fill; _bountyProbeBButton.Margin = new Padding(5, 3, 0, 3); _bountyProbeBButton.Click += delegate { ExportSelectedBountyProbeSafe(true); };
            probes.Controls.Add(_bountyProbeAButton, 0, 0); probes.Controls.Add(_bountyProbeBButton, 1, 0); grid.Controls.Add(probes, 0, 6);
            return card;
        }

        private Panel BuildAssistPage()
        {
            Panel page = NewPage();
            TableLayoutPanel layout = new TableLayoutPanel(); layout.Dock = DockStyle.Fill; layout.Margin = Padding.Empty; layout.ColumnCount = 1; layout.RowCount = 2; layout.BackColor = Theme.Background; layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64F)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F)); page.Controls.Add(layout);
            layout.Controls.Add(PageTitle("辅助", "v0.1.2：CE WalkSpeed + IMIG SprintSpeed 精确输出 Hook；跳高连续切倍率；Stealth 保留"), 0, 0);
            FlowLayoutPanel list = new FlowLayoutPanel(); list.Dock = DockStyle.Fill; list.FlowDirection = FlowDirection.TopDown; list.WrapContents = false; list.AutoScroll = true; list.BackColor = Theme.Background; list.Margin = Padding.Empty; layout.Controls.Add(list, 0, 1);

            _stealthToggle = new ToggleSwitch(); _stealthToggle.SurfaceColor = Theme.Card; _stealthToggle.Margin = new Padding(0, 14, 8, 0);
            _stealthToggle.ValueChanged += delegate
            {
                if (_closing || !_game.IsConnected) return;
                bool enabled = _stealthToggle.Checked;
                RunBusy(delegate { _game.SetInvisibleToEnemies(enabled); Log("完全隐身 → " + (enabled ? "开启" : "关闭") + "；当前 1.55 CE Stealth 消费点 gamedll+0x6C4D22。"); });
            };
            list.Controls.Add(BuildFeatureRow("完全隐身 / 全时段 Stealth", "组合当前 1.55 XXXeLxxx Stealth [day/night] (gamedll+0x6C4D22) 与 Volatile Visibility (gamedll+0xB82843) 两条真实消费链；不再写 player+0x10B4。普通/特殊/夜魔覆盖更完整，Hellraid 独立 AI 仍以实测为准。", _stealthToggle));

            _runPicker = NewPicker(1, 10); RoundedButton runApply = ActionButton("应用倍率", 104, true); runApply.SurfaceColor = Theme.Card; runApply.Click += delegate { ApplyRunFactorSafe(); };
            list.Controls.Add(BuildPickerRow("奔跑速度", "x1–x10：运算 Hook + 捕获真实冲刺参数 RDX+0x08 + 速度上限/加速度参数。点‘检查命中’可查看实际参数原值与读回值；Hook 命中并不等于实际位移生效。", _runPicker, runApply));

            _runHitStatus = Theme.Label("跑动后点击“检查命中”读取原生执行标志；不依赖 CPU 轮询。", 8F, FontStyle.Regular, Theme.Muted);
            RoundedPanel hitCard = BaseCard(0, 60);
            TableLayoutPanel hitRow = new TableLayoutPanel(); hitRow.Dock = DockStyle.Fill; hitRow.ColumnCount = 2; hitRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F)); hitRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112F));
            _runHitStatus.Dock = DockStyle.Fill; _runHitStatus.TextAlign = ContentAlignment.MiddleLeft;
            RoundedButton checkSpeed = ActionButton("检查命中", 102, false); checkSpeed.Dock = DockStyle.Fill; checkSpeed.Click += delegate { if (!RequireConnection()) return; RunBusy(delegate { string status = _game.GetRunSpeedStatusV011(); _runHitStatus.Text = status; Log("跑速 Hook 命中检查：" + status); }); };
            hitRow.Controls.Add(_runHitStatus, 0, 0); hitRow.Controls.Add(checkSpeed, 1, 0); hitCard.Controls.Add(hitRow); list.Controls.Add(hitCard);

            _jumpPicker = NewPicker(1, 10); RoundedButton jumpApply = ActionButton("应用倍率", 104, true); jumpApply.SurfaceColor = Theme.Card; jumpApply.Click += delegate { ApplyJumpFactorSafe(); };
            list.Controls.Add(BuildPickerRow("跳跃高度", "x1-x10：使用当前 1.55 Jump AOB，Patch 点 gamedll+0xD4EC6A。AOB 只在首次安装时验证，之后可直接 x2→x3→x10 实时改倍率；x1 恢复原字节。", _jumpPicker, jumpApply));

            ToggleSwitch challengeTimer = new ToggleSwitch(); challengeTimer.SurfaceColor = Theme.Card; challengeTimer.Margin = new Padding(0, 14, 8, 0);
            challengeTimer.ValueChanged += delegate
            {
                if (_closing || !_game.IsConnected) return;
                bool enabled = challengeTimer.Checked;
                RunBusy(delegate { _game.SetFreezeChallengeTimer(enabled); Log("锁定挑战计时器 → " + (enabled ? "开启" : "关闭") + "；gamedll+0x538D59。不会停止整个 Level。" ); });
            };
            list.Controls.Add(BuildFeatureRow("锁定挑战计时器", "复刻当前 1.55 CT 的 Challenge Timer 消费点 gamedll+0x538D59。仅影响挑战倒计时，不再调用旧版 ILevel::TimerStop，因此不会把整个游戏模拟一起卡住。", challengeTimer));

            ToggleSwitch missionTimer = new ToggleSwitch(); missionTimer.SurfaceColor = Theme.Card; missionTimer.Margin = new Padding(0, 14, 8, 0);
            missionTimer.ValueChanged += delegate
            {
                if (_closing || !_game.IsConnected) return;
                bool enabled = missionTimer.Checked;
                RunBusy(delegate { _game.SetFreezeMissionTimer(enabled); Log("锁定任务计时器 → " + (enabled ? "开启" : "关闭") + "；gamedll+0x48C638。" ); });
            };
            list.Controls.Add(BuildFeatureRow("锁定任务计时器", "复刻当前 1.55 CT 的 Mission Time 路径 gamedll+0x48C638，覆盖 Prison / Bozak 等使用任务计时器的流程；关闭时恢复原始机器码。", missionTimer));

            list.Controls.Add(BuildWeaponCard());
            BindResponsiveList(list);
            return page;
        }

        private Control BuildFeatureRow(string title, string subtitle, Control control)
        {
            RoundedPanel card = BaseCard(0, 82); TableLayoutPanel g = TwoColumnCardLayout(); card.Controls.Add(g);
            g.Controls.Add(CardText(title, subtitle), 0, 0); control.Anchor = AnchorStyles.Right; g.Controls.Add(control, 1, 0);
            BindTwoColumnTextCardHeight(card, subtitle, 82);
            return card;
        }

        private Control BuildPickerRow(string title, string subtitle, NumberPicker picker, RoundedButton apply)
        {
            RoundedPanel card = BaseCard(0, 84); TableLayoutPanel g = TwoColumnCardLayout(); card.Controls.Add(g); g.Controls.Add(CardText(title, subtitle), 0, 0);
            FlowLayoutPanel right = new FlowLayoutPanel(); right.Dock = DockStyle.Fill; right.FlowDirection = FlowDirection.LeftToRight; right.WrapContents = false; right.BackColor = Theme.Card; right.Padding = new Padding(0, 7, 0, 0); picker.Margin = new Padding(0, 0, 8, 0); apply.Margin = Padding.Empty; right.Controls.Add(picker); right.Controls.Add(apply); g.Controls.Add(right, 1, 0);
            BindTwoColumnTextCardHeight(card, subtitle, 84);
            return card;
        }

        private Control BuildWeaponCard()
        {
            RoundedPanel card = BaseCard(0, 176); card.Padding = new Padding(16, 12, 16, 12);
            TableLayoutPanel g = new TableLayoutPanel(); g.Dock = DockStyle.Fill; g.Margin = Padding.Empty; g.ColumnCount = 1; g.RowCount = 4; g.BackColor = Theme.Card; g.RowStyles.Add(new RowStyle(SizeType.Absolute, 28F)); g.RowStyles.Add(new RowStyle(SizeType.Absolute, 42F)); g.RowStyles.Add(new RowStyle(SizeType.Absolute, 44F)); g.RowStyles.Add(new RowStyle(SizeType.Percent, 100F)); card.Controls.Add(g);
            Label title = Theme.Label("传奇 / 默认武器", 10.5F, FontStyle.Bold, Theme.Text); title.Dock = DockStyle.Fill; g.Controls.Add(title, 0, 0);
            _weaponDetail = Theme.Label("按原生库存接口添加武器，并尝试将实例品质设为橙色；伤害/耐久等可在‘背包 / 库存’页修改。", 8F, FontStyle.Regular, Theme.Text); _weaponDetail.Dock = DockStyle.Fill; g.Controls.Add(_weaponDetail, 0, 1);
            TableLayoutPanel row = new TableLayoutPanel(); row.Dock = DockStyle.Fill; row.Margin = Padding.Empty; row.ColumnCount = 3; row.RowCount = 1; row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28F)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 72F)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 155F));
            _weaponCategory = DarkCombo(); _weaponPicker = DarkCombo();
            List<string> cats = _weapons.Select(x => x.category ?? "其他").Distinct().OrderBy(x => x).ToList(); foreach (string c in cats) _weaponCategory.Items.Add(c); if (_weaponCategory.Items.Count > 0) _weaponCategory.SelectedIndex = 0;
            _weaponCategory.SelectedIndexChanged += delegate { RefreshWeaponPicker(); }; _weaponPicker.SelectedIndexChanged += delegate { WeaponCatalogItem w = _weaponPicker.SelectedItem as WeaponCatalogItem; if (w != null) _weaponDetail.Text = "目标：" + w.internalId + "。添加后尝试写入品质 4（橙色）；可以在背包/库存里改数量、耐久、修理次数及基础伤害。"; };
            RoundedButton add = ActionButton("添加橙色武器", 146, true); add.SurfaceColor = Theme.Card; add.Dock = DockStyle.Fill; add.Margin = new Padding(8, 4, 0, 4); add.Click += delegate { GiveSelectedWeaponSafe(); };
            row.Controls.Add(_weaponCategory, 0, 0); row.Controls.Add(_weaponPicker, 1, 0); row.Controls.Add(add, 2, 0); g.Controls.Add(row, 0, 2);
            Label note = Theme.Label("‘Legendary1’只是内部定义名，不一定是橙色掉落。官方 Data0 里部分传奇模板仍指定白色。自动转橙仅对已核验实例操作；其他武器可在背包页设品质 4。", 8F, FontStyle.Regular, Theme.Muted);
            note.Dock = DockStyle.Fill; g.Controls.Add(note, 0, 3);
            bool resizing = false;
            Action fitWeaponCard = delegate
            {
                if (resizing || card.IsDisposed) return;
                resizing = true;
                try
                {
                    int textWidth = Math.Max(240, card.ClientSize.Width - card.Padding.Horizontal - 8);
                    int detailHeight = TextRenderer.MeasureText(_weaponDetail.Text ?? "", _weaponDetail.Font,
                        new Size(textWidth, 600), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix).Height + 6;
                    int noteHeight = TextRenderer.MeasureText(note.Text ?? "", note.Font,
                        new Size(textWidth, 600), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix).Height + 6;
                    g.RowStyles[1].Height = Math.Max(36, detailHeight);
                    int desired = card.Padding.Vertical + 28 + Math.Max(36, detailHeight) + 44 + Math.Max(30, noteHeight) + 6;
                    if (Math.Abs(card.Height - desired) > 1) card.Height = desired;
                }
                finally { resizing = false; }
            };
            card.ClientSizeChanged += delegate { fitWeaponCard(); };
            _weaponDetail.TextChanged += delegate { fitWeaponCard(); };
            fitWeaponCard();
            RefreshWeaponPicker();
            return card;
        }

        private Panel BuildCollectiblesPage()
        {
            Panel page = NewPage();
            TableLayoutPanel layout = new TableLayoutPanel(); layout.Dock = DockStyle.Fill; layout.Margin = Padding.Empty; layout.ColumnCount = 1; layout.RowCount = 3; layout.BackColor = Theme.Background;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64F)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52F)); page.Controls.Add(layout);
            layout.Controls.Add(PageTitle("收集品 / 蓝图", "逐项进度及当前存档快照；无法核实的项目显示 ?"), 0, 0);

            TableLayoutPanel body = new TableLayoutPanel(); body.Dock = DockStyle.Fill; body.Margin = Padding.Empty; body.ColumnCount = 2; body.RowCount = 1; body.BackColor = Theme.Background; body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62F)); body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38F));
            _collectibleGrid = new DataGridView(); ConfigureBaseGrid(_collectibleGrid); _collectibleGrid.Columns.Add("Scope", "范围"); _collectibleGrid.Columns.Add("Category", "分类"); _collectibleGrid.Columns.Add("Progress", "进度"); _collectibleGrid.Columns.Add("State", "状态"); _collectibleGrid.Columns[0].FillWeight = 95; _collectibleGrid.Columns[1].FillWeight = 165; _collectibleGrid.Columns[2].FillWeight = 75; _collectibleGrid.Columns[3].FillWeight = 80; _collectibleGrid.SelectionChanged += delegate { UpdateCollectibleDetail(); }; body.Controls.Add(_collectibleGrid, 0, 0);

            RoundedPanel detail = new RoundedPanel(); detail.Dock = DockStyle.Fill; detail.Margin = new Padding(10, 0, 0, 0); detail.Padding = new Padding(14, 12, 14, 12); detail.BackColor = Theme.Card; detail.BorderColor = Theme.Line;
            TableLayoutPanel dg = new TableLayoutPanel(); dg.Dock = DockStyle.Fill; dg.Margin = Padding.Empty; dg.ColumnCount = 1; dg.RowCount = 4; dg.BackColor = Theme.Card; dg.RowStyles.Add(new RowStyle(SizeType.Absolute, 32F)); dg.RowStyles.Add(new RowStyle(SizeType.Absolute, 52F)); dg.RowStyles.Add(new RowStyle(SizeType.Percent, 100F)); dg.RowStyles.Add(new RowStyle(SizeType.Absolute, 48F)); detail.Controls.Add(dg);
            _collectibleName = Theme.Label("请选择分类", 11F, FontStyle.Bold, Theme.Text); _collectibleName.Dock = DockStyle.Fill; dg.Controls.Add(_collectibleName, 0, 0);
            _collectibleMeta = Theme.Label("", 8F, FontStyle.Regular, Theme.Muted); _collectibleMeta.Dock = DockStyle.Fill; dg.Controls.Add(_collectibleMeta, 0, 1);
            _collectibleDetail = new TextBox(); _collectibleDetail.Dock = DockStyle.Fill; _collectibleDetail.Multiline = true; _collectibleDetail.ReadOnly = true; _collectibleDetail.ScrollBars = ScrollBars.Vertical; _collectibleDetail.BorderStyle = BorderStyle.None; _collectibleDetail.BackColor = Theme.Card; _collectibleDetail.ForeColor = Theme.Text; _collectibleDetail.Font = new Font("Microsoft YaHei UI", 8.3F); dg.Controls.Add(_collectibleDetail, 0, 2);
            TableLayoutPanel da = new TableLayoutPanel(); da.Dock = DockStyle.Fill; da.Margin = Padding.Empty; da.ColumnCount = 2; da.RowCount = 1; da.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F)); da.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            _collectibleOneButton = ActionButton("补 1 项", 96, false); _collectibleOneButton.SurfaceColor = Theme.Card; _collectibleOneButton.Dock = DockStyle.Fill; _collectibleOneButton.Margin = new Padding(0, 5, 5, 5); _collectibleOneButton.Click += delegate { CompleteOneCollectibleSafe(); };
            _collectibleAllButton = ActionButton("补齐分类", 104, true); _collectibleAllButton.SurfaceColor = Theme.Card; _collectibleAllButton.Dock = DockStyle.Fill; _collectibleAllButton.Margin = new Padding(5, 5, 0, 5); _collectibleAllButton.Click += delegate { CompleteCollectibleCategorySafe(); }; da.Controls.Add(_collectibleOneButton, 0, 0); da.Controls.Add(_collectibleAllButton, 1, 0); dg.Controls.Add(da, 0, 3);
            body.Controls.Add(detail, 1, 0); layout.Controls.Add(body, 0, 1);

            // Two rows: explanation uses the whole width; actions get equal, scalable cells.
            // Previous fixed 120 / 170px columns clipped text at higher DPI.
            layout.RowStyles[2].Height = 102F;
            TableLayoutPanel footer = new TableLayoutPanel(); footer.Dock = DockStyle.Fill; footer.Margin = new Padding(0, 8, 0, 0);
            footer.ColumnCount = 3; footer.RowCount = 2;
            footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 44F)); footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.3F)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.3F)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.4F));
            Label note = Theme.Label("已经收齐失踪人口 / 奇怪岩石也能直接导出当前存档快照；蓝图 ? 代表尚未定位已学状态，不代表未学。", 8F, FontStyle.Regular, Theme.Muted);
            note.Dock = DockStyle.Fill; note.TextAlign = ContentAlignment.MiddleLeft;
            footer.Controls.Add(note, 0, 0); footer.SetColumnSpan(note, 3);
            _collectibleRefreshButton = ActionButton("刷新进度", 110, false);
            _collectibleRefreshButton.SurfaceColor = Theme.Background; _collectibleRefreshButton.Dock = DockStyle.Fill;
            _collectibleRefreshButton.Margin = new Padding(0, 3, 6, 3);
            _collectibleRefreshButton.Click += delegate { RefreshCollectiblesSafe(); }; footer.Controls.Add(_collectibleRefreshButton, 0, 1);
            _collectibleSnapshotButton = ActionButton("导出当前存档快照", 158, false);
            _collectibleSnapshotButton.SurfaceColor = Theme.Background; _collectibleSnapshotButton.Dock = DockStyle.Fill;
            _collectibleSnapshotButton.Margin = new Padding(3, 3, 3, 3);
            _collectibleSnapshotButton.Click += delegate { ExportCollectedSnapshotSafe(); }; footer.Controls.Add(_collectibleSnapshotButton, 1, 1);
            _collectibleAllSupportedButton = ActionButton("补齐全部已支持", 156, true);
            _collectibleAllSupportedButton.SurfaceColor = Theme.Background; _collectibleAllSupportedButton.Dock = DockStyle.Fill;
            _collectibleAllSupportedButton.Margin = new Padding(6, 3, 0, 3);
            _collectibleAllSupportedButton.Click += delegate { CompleteAllCollectiblesSafe(); }; footer.Controls.Add(_collectibleAllSupportedButton, 2, 1);
            layout.Controls.Add(footer, 0, 2);
            return page;
        }

        private Panel BuildHellraidPage()
        {
            Panel page = NewPage(); TableLayoutPanel layout = new TableLayoutPanel(); layout.Dock = DockStyle.Fill; layout.Margin = Padding.Empty; layout.ColumnCount = 1; layout.RowCount = 5; layout.BackColor = Theme.Background; layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64F)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 112F)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 92F)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 96F)); page.Controls.Add(layout);
            layout.Controls.Add(PageTitle("Hellraid", "Coins / Easy Craft / 附近敌人屠杀采用 1.55 原生物品名称 ABI / CT 路径；专属悬赏共用后端"), 0, 0);

            RoundedPanel coinCard = BaseCard(0, 102); coinCard.Dock = DockStyle.Fill; coinCard.Margin = new Padding(0, 0, 0, 10); TableLayoutPanel cg = new TableLayoutPanel(); cg.Dock = DockStyle.Fill; cg.Margin = Padding.Empty; cg.Padding = new Padding(14, 10, 14, 10); cg.ColumnCount = 2; cg.RowCount = 1; cg.BackColor = Theme.Card; cg.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F)); cg.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 360F)); coinCard.Controls.Add(cg);
            cg.Controls.Add(CardText("Hellraid Coins", "通过原生库存 AddItem 发送校验过的 cash_hellraid_coin 名称结构及数量；请实际核对增加数量。"), 0, 0);
            FlowLayoutPanel coins = new FlowLayoutPanel(); coins.Dock = DockStyle.Fill; coins.FlowDirection = FlowDirection.LeftToRight; coins.WrapContents = false; coins.BackColor = Theme.Card; coins.Padding = new Padding(0, 14, 0, 0);
            foreach (int amount in new int[] { 1000, 10000, 666666 }) { int a = amount; RoundedButton b = ActionButton("+" + a.ToString("N0"), 104, a == 666666); b.SurfaceColor = Theme.Card; b.Margin = new Padding(3, 0, 3, 0); b.Click += delegate { AddHellraidCoinsSafe(a); }; coins.Controls.Add(b); }
            cg.Controls.Add(coins, 1, 0); layout.Controls.Add(coinCard, 0, 1);

            RoundedPanel tools = BaseCard(0, 82); tools.Dock = DockStyle.Fill; tools.Margin = new Padding(0, 0, 0, 10); TableLayoutPanel tg = new TableLayoutPanel(); tg.Dock = DockStyle.Fill; tg.ColumnCount = 4; tg.RowCount = 1; tg.BackColor = Theme.Card; tg.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F)); tg.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70F)); tg.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F)); tg.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70F)); tools.Controls.Add(tg);
            tg.Controls.Add(CardText("Hellraid / 全蓝图简易制作", "复刻当前 1.55 CT 的 Easy Craft 三个精确入口。"), 0, 0); ToggleSwitch craft = new ToggleSwitch(); craft.SurfaceColor = Theme.Card; craft.Margin = new Padding(8, 14, 8, 0); craft.ValueChanged += delegate { if (!_game.IsConnected) return; bool on = craft.Checked; RunBusy(delegate { _game.SetEasyCraft(on); Log("Easy Craft → " + (on ? "开启" : "关闭")); }); }; tg.Controls.Add(craft, 1, 0);
            tg.Controls.Add(CardText("Hellraid 骷髅 / 附近敌人屠杀", "当前 1.55 CE 35m 敌人生命消费点；用于补 Wand 漏掉的 Hellraid 敌人。"), 2, 0); ToggleSwitch kill = new ToggleSwitch(); kill.SurfaceColor = Theme.Card; kill.Margin = new Padding(8, 14, 8, 0); kill.ValueChanged += delegate { if (!_game.IsConnected) return; bool on = kill.Checked; RunBusy(delegate { _game.SetNearbyEnemySlaughter(on); Log("附近敌人屠杀 → " + (on ? "开启" : "关闭")); }); }; tg.Controls.Add(kill, 3, 0); layout.Controls.Add(tools, 0, 2);

            RoundedPanel bountyCard = BaseCard(0, 0); bountyCard.Dock = DockStyle.Fill; bountyCard.Margin = new Padding(0, 0, 0, 10); bountyCard.Padding = new Padding(12); TableLayoutPanel bg = new TableLayoutPanel(); bg.Dock = DockStyle.Fill; bg.Margin = Padding.Empty; bg.ColumnCount = 1; bg.RowCount = 2; bg.BackColor = Theme.Card; bg.RowStyles.Add(new RowStyle(SizeType.Absolute, 40F)); bg.RowStyles.Add(new RowStyle(SizeType.Percent, 100F)); bountyCard.Controls.Add(bg);
            TableLayoutPanel bh = new TableLayoutPanel(); bh.Dock = DockStyle.Fill; bh.ColumnCount = 2; bh.RowCount = 1; bh.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F)); bh.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100F)); Label bt = Theme.Label("Hellraid 专属悬赏", 10.5F, FontStyle.Bold, Theme.Text); bt.Dock = DockStyle.Fill; bt.TextAlign = ContentAlignment.MiddleLeft; bh.Controls.Add(bt, 0, 0); RoundedButton hr = ActionButton("刷新", 88, false); hr.SurfaceColor = Theme.Card; hr.Dock = DockStyle.Fill; hr.Margin = new Padding(6, 3, 0, 3); hr.Click += delegate { RefreshHellraidBountiesSafe(); }; bh.Controls.Add(hr, 1, 0); bg.Controls.Add(bh, 0, 0);
            _hellraidGrid = new DataGridView(); ConfigureBaseGrid(_hellraidGrid); _hellraidGrid.Columns.Add("Status", "状态"); _hellraidGrid.Columns.Add("Name", "名称"); _hellraidGrid.Columns.Add("Progress", "进度"); _hellraidGrid.Columns.Add("Reward", "奖励"); _hellraidGrid.Columns.Add("Id", "ID"); _hellraidGrid.Columns[0].FillWeight = 60; _hellraidGrid.Columns[1].FillWeight = 150; _hellraidGrid.Columns[2].FillWeight = 70; _hellraidGrid.Columns[3].FillWeight = 150; _hellraidGrid.Columns[4].FillWeight = 55; bg.Controls.Add(_hellraidGrid, 0, 1); layout.Controls.Add(bountyCard, 0, 3);

            RoundedPanel status = BaseCard(0, 86); status.Dock = DockStyle.Fill; status.Padding = new Padding(14, 10, 14, 10); _hellraidStatus = Theme.Label("当前 1.55 CT/IMIG 精确 AOB 已接入。骷髅屠杀使用生命消费点而不是 Wand 的僵尸类型过滤，因此目标就是覆盖 Hellraid 骷髅；若某个 Boss/法师使用独立生命类，日志里再单独补类型。", 8F, FontStyle.Regular, Theme.Warning); _hellraidStatus.Dock = DockStyle.Fill; status.Controls.Add(_hellraidStatus); layout.Controls.Add(status, 0, 4);
            return page;
        }

        private Panel BuildLogPage()
        {
            Panel page = NewPage(); TableLayoutPanel l = new TableLayoutPanel(); l.Dock = DockStyle.Fill; l.Margin = Padding.Empty; l.ColumnCount = 1; l.RowCount = 2; l.BackColor = Theme.Background; l.RowStyles.Add(new RowStyle(SizeType.Absolute, 64F)); l.RowStyles.Add(new RowStyle(SizeType.Percent, 100F)); page.Controls.Add(l); l.Controls.Add(PageTitle("日志", "连接、任务读取、深度诊断与安全保护状态都会记录在这里"), 0, 0);
            _logBox = new TextBox(); _logBox.Dock = DockStyle.Fill; _logBox.Multiline = true; _logBox.ReadOnly = true; _logBox.ScrollBars = ScrollBars.Vertical; _logBox.BackColor = Color.FromArgb(8, 13, 23); _logBox.ForeColor = Theme.Text; _logBox.BorderStyle = BorderStyle.FixedSingle; _logBox.Font = new Font("Consolas", 9F); l.Controls.Add(_logBox, 0, 1); return page;
        }

        private Panel BuildSettingsPage()
        {
            Panel page = NewPage(); FlowLayoutPanel list = new FlowLayoutPanel(); list.Dock = DockStyle.Fill; list.FlowDirection = FlowDirection.TopDown; list.WrapContents = false; list.AutoScroll = true; list.BackColor = Theme.Background; page.Controls.Add(list);
            Control title = PageTitle("设置 / 诊断", "精确 CT/AOB 已用于实现；保留 A/B 只用于尚未覆盖的运行时差异"); title.Height = 64; list.Controls.Add(title);

            RoundedPanel build = BaseCard(0, 94); Label info = Theme.Label("目标 gamedll SHA-256\r\n" + GameClient.ExpectedHash + "\r\n\r\nv0.1.2：跑速分别使用 XXXeLxxx WalkSpeed / IMIG SprintSpeed1 指令点 Hook，不再定时改写内存速度参数；武器从错误 BaseItem* 修正为 NameId{char*,len} 参数。特殊收集品与蓝图支持真实 A/B 差异诊断。", 8.5F, FontStyle.Regular, Theme.Text); info.Dock = DockStyle.Fill; info.AutoEllipsis = false; build.Controls.Add(info); list.Controls.Add(build); BindWrappedLabelCard(build, info, 76);

            RoundedPanel probe = BaseCard(0, 150); TableLayoutPanel pg = new TableLayoutPanel(); pg.Dock = DockStyle.Fill; pg.Margin = Padding.Empty; pg.ColumnCount = 1; pg.RowCount = 4; pg.BackColor = Theme.Card; pg.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F)); pg.RowStyles.Add(new RowStyle(SizeType.Absolute, 42F)); pg.RowStyles.Add(new RowStyle(SizeType.Absolute, 38F)); pg.RowStyles.Add(new RowStyle(SizeType.Percent, 100F)); probe.Controls.Add(pg);
            Label pt = Theme.Label("深度 A/B · FLiNG / 游戏正常动作定位", 10.5F, FontStyle.Bold, Theme.Text); pt.Dock = DockStyle.Fill; pg.Controls.Add(pt, 0, 0);
            TableLayoutPanel row = new TableLayoutPanel(); row.Dock = DockStyle.Fill; row.Margin = Padding.Empty; row.ColumnCount = 3; row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150F)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170F));
            _probeKind = DarkCombo(); foreach (string s in new string[] { "隐身 / Stealth", "奔跑速度", "跳跃高度", "武器 / 库存" }) _probeKind.Items.Add(s); _probeKind.SelectedIndex = 0; row.Controls.Add(_probeKind, 0, 0);
            RoundedButton pa = ActionButton("采样 A · 基线", 138, false); pa.SurfaceColor = Theme.Card; pa.Dock = DockStyle.Fill; pa.Margin = new Padding(8, 4, 4, 4); pa.Click += delegate { CaptureFeatureProbeSafe(false); }; row.Controls.Add(pa, 1, 0);
            RoundedButton pb = ActionButton("采样 B · 对比并导出", 160, true); pb.SurfaceColor = Theme.Card; pb.Dock = DockStyle.Fill; pb.Margin = new Padding(4, 4, 0, 4); pb.Click += delegate { CaptureFeatureProbeSafe(true); }; row.Controls.Add(pb, 2, 0); pg.Controls.Add(row, 0, 1);
            Label hint = Theme.Label("这些 A/B 仅用于校验/继续逆向：奔跑采用 IMIG SprintSpeed1 指针路径；跳高/Stealth 采用当前 1.55 精确入口；武器采用 BaseItem Definition + B_GiveWeapon。正常使用不需要每次采样。", 8F, FontStyle.Regular, Theme.Muted); hint.Dock = DockStyle.Fill; pg.Controls.Add(hint, 0, 2);
            _probeStatus = Theme.Label("尚未采样。", 8F, FontStyle.Regular, Theme.Warning); _probeStatus.Dock = DockStyle.Fill; pg.Controls.Add(_probeStatus, 0, 3); list.Controls.Add(probe);

            RoundedPanel research = BaseCard(0, 104); Label r = Theme.Label("Quest：你的当前存档主线/支线均已完成，目录继续保留只读，避免在没有活动对象时猜 Quest ABI。\r\n悬赏：已根据实测 Progress+0x70 与 BountyManager+0x6F0 游戏线程评估队列重做完成链。\r\nCT 参考：XXXeLxxx 1.55 用于 Stealth / Volatile Visibility / Jumps / Challenge & Mission Timer / Easy Craft / 35m Kill；IMIG 用于 BaseItem/背包结构和 Sprint/Jump 交叉验证。", 8.5F, FontStyle.Regular, Theme.Text); r.Dock = DockStyle.Fill; r.AutoEllipsis = false; research.Controls.Add(r); list.Controls.Add(research); BindWrappedLabelCard(research, r, 82);
            BindResponsiveList(list);
            return page;
        }

        private void OpenFeatureProbe(string kind)
        {
            ShowPage("设置");
            if (_probeKind != null)
            {
                for (int i = 0; i < _probeKind.Items.Count; i++)
                {
                    if (String.Equals(Convert.ToString(_probeKind.Items[i]), kind, StringComparison.OrdinalIgnoreCase))
                    {
                        _probeKind.SelectedIndex = i; break;
                    }
                }
            }
            if (_probeStatus != null) _probeStatus.Text = "已选择 “" + kind + "”。先保持目标功能关闭/动作前点 A，再只改变这一项后点 B。";
        }

        private void CaptureFeatureProbeSafe(bool compare)
        {
            if (!RequireConnection()) return;
            string kind = _probeKind == null || _probeKind.SelectedItem == null ? "Probe" : _probeKind.SelectedItem.ToString();
            RunBusy(delegate
            {
                string text = _game.CaptureFeatureProbe(kind, compare);
                if (!compare)
                {
                    if (_probeStatus != null) _probeStatus.Text = "已保存 “" + kind + "” 的 A 基线。现在只改变这一项，然后点 B。";
                    Log("诊断采样 A：" + kind + "。");
                }
                else
                {
                    string safe = kind.Replace("/", "_").Replace("\\", "_").Replace(" ", "_");
                    string path = Path.Combine(DiagnosticsDirectory(), "FeatureProbe_" + safe + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt");
                    File.WriteAllText(path, text, Encoding.UTF8);
                    if (_probeStatus != null) _probeStatus.Text = "已导出：" + path;
                    Log("诊断采样 B 已导出：" + path);
                    MessageBox.Show(this, "已导出：\r\n" + path + "\r\n\r\n把这个文件发给我即可。", "A/B 对比完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            });
        }

        private void BindResponsiveList(FlowLayoutPanel list)
        {
            if (list == null) return;
            int lastWidth = -1;
            bool inLayout = false;
            Action resize = delegate
            {
                if (inLayout || list.IsDisposed) return;
                int reserve = SystemInformation.VerticalScrollBarWidth + 12;
                int width = Math.Max(320, list.ClientSize.Width - list.Padding.Horizontal - reserve);
                if (lastWidth == width && list.Controls.Count > 0 && list.Controls[0].Width == width) return;
                lastWidth = width;
                inLayout = true;
                try
                {
                    list.SuspendLayout();
                    foreach (Control c in list.Controls)
                        if (c != null && c.Width != width) c.Width = width;
                }
                finally { list.ResumeLayout(false); inLayout = false; }
            };
            list.ClientSizeChanged += delegate { resize(); };
            list.ControlAdded += delegate { resize(); };
            resize();
        }

        private void BindTwoColumnTextCardHeight(RoundedPanel card, string subtitle, int minHeight)
        {
            if (card == null) return;
            bool fitting = false;
            int lastWidth = -1;
            Action fit = delegate
            {
                if (fitting || card.IsDisposed) return;
                int width = Math.Max(140, card.ClientSize.Width - card.Padding.Horizontal - 288 - 8);
                if (width == lastWidth) return;
                lastWidth = width;
                fitting = true;
                try
                {
                    using (Font bodyFont = new Font("Microsoft YaHei UI", 8F))
                    {
                        Size body = TextRenderer.MeasureText(subtitle ?? "", bodyFont, new Size(width, 1200),
                            TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix);
                        int desired = Math.Max(minHeight, card.Padding.Vertical + 28 + body.Height + 6);
                        if (Math.Abs(card.Height - desired) > 1) card.Height = desired;
                    }
                }
                finally { fitting = false; }
            };
            card.ClientSizeChanged += delegate { fit(); };
            fit();
        }

        private void BindWrappedLabelCard(RoundedPanel card, Label label, int minHeight)
        {
            if (card == null || label == null) return;
            bool fitting = false;
            Action fit = delegate
            {
                if (fitting || card.IsDisposed) return;
                fitting = true;
                try
                {
                    int width = Math.Max(180, card.ClientSize.Width - card.Padding.Horizontal - 4);
                    Size measured = TextRenderer.MeasureText(label.Text ?? "", label.Font, new Size(width, 2000),
                        TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix);
                    int desired = Math.Max(minHeight, card.Padding.Vertical + measured.Height + 8);
                    if (Math.Abs(card.Height - desired) > 1) card.Height = desired;
                }
                finally { fitting = false; }
            };
            label.AutoEllipsis = false;
            card.ClientSizeChanged += delegate { fit(); };
            label.TextChanged += delegate { fit(); };
            fit();
        }

        private RoundedPanel BaseCard(int width, int height)
        {
            RoundedPanel card = new RoundedPanel(); if (width > 0) card.Width = width; if (height > 0) card.Height = height; card.Margin = new Padding(0, 0, 8, 10); card.Padding = new Padding(16, 10, 16, 10); card.BackColor = Theme.Card; card.BorderColor = Theme.Line; card.SurfaceColor = Theme.Background; return card;
        }

        private TableLayoutPanel TwoColumnCardLayout()
        {
            TableLayoutPanel g = new TableLayoutPanel(); g.Dock = DockStyle.Fill; g.Margin = Padding.Empty; g.ColumnCount = 2; g.RowCount = 1; g.BackColor = Theme.Card; g.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F)); g.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 288F)); return g;
        }

        private Control CardText(string title, string subtitle)
        {
            TableLayoutPanel t = new TableLayoutPanel(); t.Dock = DockStyle.Fill; t.Margin = Padding.Empty; t.ColumnCount = 1; t.RowCount = 2; t.BackColor = Theme.Card; t.RowStyles.Add(new RowStyle(SizeType.Absolute, 28F)); t.RowStyles.Add(new RowStyle(SizeType.Percent, 100F)); Label a = Theme.Label(title, 10.5F, FontStyle.Bold, Theme.Text); a.AutoSize = false; a.AutoEllipsis = false; a.Dock = DockStyle.Fill; Label b = Theme.Label(subtitle, 8F, FontStyle.Regular, Theme.Muted); b.AutoSize = false; b.AutoEllipsis = false; b.Dock = DockStyle.Fill; t.Controls.Add(a, 0, 0); t.Controls.Add(b, 0, 1); return t;
        }

        private NumberPicker NewPicker(int min, int max) { NumberPicker p = new NumberPicker(); p.Minimum = min; p.Maximum = max; p.Value = min; p.Prefix = "x"; p.SurfaceColor = Theme.Card; return p; }
        private ComboBox DarkCombo() { ComboBox c = new ComboBox(); c.Dock = DockStyle.Fill; c.Margin = new Padding(0, 4, 8, 4); c.DropDownStyle = ComboBoxStyle.DropDownList; c.FlatStyle = FlatStyle.Flat; c.BackColor = Color.FromArgb(12, 19, 33); c.ForeColor = Theme.Text; c.Font = Theme.Normal; return c; }
        private Panel NewPage() { Panel p = new Panel(); p.BackColor = Theme.Background; p.Margin = Padding.Empty; return p; }
        private Control PageTitle(string title, string subtitle) { TableLayoutPanel t = new TableLayoutPanel(); t.Dock = DockStyle.Fill; t.Margin = Padding.Empty; t.ColumnCount = 1; t.RowCount = 2; t.RowStyles.Add(new RowStyle(SizeType.Absolute, 33F)); t.RowStyles.Add(new RowStyle(SizeType.Absolute, 27F)); Label a = Theme.Label(title, 16F, FontStyle.Bold, Theme.Text); a.AutoSize = false; a.Dock = DockStyle.Fill; Label b = Theme.Label(subtitle, 8.3F, FontStyle.Regular, Theme.Muted); b.AutoSize = false; b.Dock = DockStyle.Fill; t.Controls.Add(a, 0, 0); t.Controls.Add(b, 0, 1); return t; }
        private RoundedButton ActionButton(string text, int width, bool primary) { RoundedButton b = new RoundedButton(); b.Text = text; b.Width = width; b.Height = 36; b.ForeColor = Theme.Text; b.SurfaceColor = Theme.Background; b.NormalColor = primary ? Theme.Accent : Theme.Card2; b.HoverColor = primary ? Theme.AccentHover : Color.FromArgb(38, 52, 73); return b; }

        private void ShowPage(string name)
        {
            foreach (KeyValuePair<string, Panel> kv in _pages) kv.Value.Visible = kv.Key == name;
            foreach (KeyValuePair<string, RoundedButton> kv in _navButtons) kv.Value.NormalColor = kv.Key == name ? Theme.Accent : Theme.Sidebar;
            if (name == "任务") RefreshTaskMode();
            if (name == "收集品") RefreshCollectiblesSafe();
            if (name == "背包 / 库存" && _game.IsConnected)
            {
                try { _game.EnsureInventoryCaptureV012(); }
                catch (Exception ex) { Log("背包捕获初始化：" + ex.Message); }
            }
            if (name == "Hellraid") RefreshHellraidBountiesSafe();
        }

        private void SetTaskMode(string mode)
        {
            _taskMode = mode;
            if (_taskTabs != null) foreach (Control c in _taskTabs.Controls) { RoundedButton b = c as RoundedButton; if (b != null) { string tag = b.Tag as string; if (tag != null) b.NormalColor = tag == mode ? Theme.Accent : Theme.Card2; } }
            RefreshTaskMode();
        }

        private void ConfigureBaseGrid(DataGridView grid)
        {
            grid.Dock = DockStyle.Fill; grid.Margin = Padding.Empty; grid.BackgroundColor = Color.FromArgb(8, 13, 23); grid.BorderStyle = BorderStyle.None; grid.RowHeadersVisible = false; grid.AllowUserToAddRows = false; grid.AllowUserToDeleteRows = false; grid.AllowUserToResizeRows = false; grid.ReadOnly = true; grid.MultiSelect = false; grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect; grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill; grid.EnableHeadersVisualStyles = false; grid.ColumnHeadersHeight = 34; grid.RowTemplate.Height = 30; grid.ColumnHeadersDefaultCellStyle.BackColor = Theme.Card2; grid.ColumnHeadersDefaultCellStyle.ForeColor = Theme.Text; grid.DefaultCellStyle.BackColor = Color.FromArgb(10, 17, 29); grid.DefaultCellStyle.ForeColor = Theme.Text; grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(31, 52, 83); grid.DefaultCellStyle.SelectionForeColor = Theme.Text; grid.GridColor = Theme.Line;
        }

        private void ConfigureBountyGrid()
        {
            _taskGrid.Columns.Clear(); ConfigureBaseGrid(_taskGrid); _taskGrid.Columns.Add("Status", "状态"); _taskGrid.Columns.Add("Type", "类型"); _taskGrid.Columns.Add("Name", "名称"); _taskGrid.Columns.Add("Progress", "进度"); _taskGrid.Columns.Add("Reward", "奖励"); _taskGrid.Columns.Add("Id", "ID"); _taskGrid.Columns[0].FillWeight = 65; _taskGrid.Columns[1].FillWeight = 70; _taskGrid.Columns[2].FillWeight = 170; _taskGrid.Columns[3].FillWeight = 90; _taskGrid.Columns[4].FillWeight = 140; _taskGrid.Columns[5].FillWeight = 60;
        }

        private void ConfigureStaticGrid()
        {
            _taskGrid.Columns.Clear(); ConfigureBaseGrid(_taskGrid); _taskGrid.Columns.Add("Backend", "后端"); _taskGrid.Columns.Add("Type", "分类"); _taskGrid.Columns.Add("Name", "名称"); _taskGrid.Columns.Add("Key", "内部 Key"); _taskGrid.Columns[0].FillWeight = 80; _taskGrid.Columns[1].FillWeight = 80; _taskGrid.Columns[2].FillWeight = 180; _taskGrid.Columns[3].FillWeight = 170;
        }

        private void ConnectSafe(bool showError)
        {
            if (_busy) return;
            try
            {
                _connectionDot.ForeColor = Theme.Warning; _connectionText.Text = "连接中…"; _connectionText.ForeColor = Theme.Muted;
                _game.Connect();
                _connectionDot.Text = "●"; _connectionDot.ForeColor = Theme.Accent; _connectionText.Text = "已连接 · PID " + _game.ProcessId; _connectionText.ForeColor = Theme.Text; _connectButton.Text = "重连游戏";
                Log("已连接 DyingLightGame.exe，PID=" + _game.ProcessId + "。");
                if (_stealthToggle != null) _stealthToggle.Checked = false;
                if (_runPicker != null) _runPicker.Value = 1;
                if (_jumpPicker != null) _jumpPicker.Value = 1;
                RefreshTaskMode(); RefreshCollectiblesSafe(); RefreshHellraidBountiesSafe();
            }
            catch (Exception ex)
            {
                _connectionDot.ForeColor = Theme.Bad; _connectionText.Text = "未连接"; _connectionText.ForeColor = Theme.Bad; _connectButton.Text = "连接游戏"; Log("连接失败：" + ex.Message);
                if (showError) MessageBox.Show(this, ex.Message, "连接失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private bool RequireConnection() { if (_game.IsConnected) return true; MessageBox.Show(this, "请先连接游戏。", "尚未连接", MessageBoxButtons.OK, MessageBoxIcon.Information); return false; }
        private void RefreshTaskMode() { if (_taskGrid == null) return; if (_taskMode == "悬赏") RefreshBountiesSafe(); else RefreshStaticCatalog(_taskMode); }

        private void RefreshStaticCatalog(string category)
        {
            ConfigureStaticGrid(); _taskGrid.Rows.Clear(); _selectedBountyId = -1; _selectedBountyPtr = IntPtr.Zero; _selectedBountyState = -1; _selectedQuestItem = null; _acceptButton.Text = "接取选中"; _acceptButton.Enabled = false;
            _completeButton.Text = "深度采集 Quest"; _completeButton.Enabled = false; _completeAllButton.Text = "Quest 总诊断"; _completeAllButton.Enabled = _game.IsConnected; if (_bountyProbeAButton != null) { _bountyProbeAButton.Enabled = false; _bountyProbeAButton.Text = "采样 A"; } if (_bountyProbeBButton != null) { _bountyProbeBButton.Enabled = false; _bountyProbeBButton.Text = "采样 B"; }
            List<TaskCatalogItem> list = null; if (_taskCatalog.categories != null) _taskCatalog.categories.TryGetValue(category, out list); if (list == null) list = new List<TaskCatalogItem>();
            foreach (TaskCatalogItem item in list) { int row = _taskGrid.Rows.Add("QuestManager 待定位", category, item.name, item.key); _taskGrid.Rows[row].Tag = item; }
            _detailName.Text = category + "任务目录"; _detailMeta.Text = "真实任务 key：" + list.Count + " 项 · 可导出运行时探针"; _detailReward.Text = "选中具体任务后点“深度采集 Quest”，把 Diagnostics 文本发回来。"; _objectives.Text = "当前 DLL 已确认存在 Quests::QuestManager、Quest::Start、Quest::Complete、QuestManager::AddQuestState、MenuInvokeQuestState 等接口。\r\n\r\nv0.1.0 已从当前 DLL 构造函数精确定位 QuestManager 单例 gamedll+0x1D1C790；会校验其 vtable 并读取任务容器，但仍不会猜状态 RVA 写任务。";
            if (_taskGrid.Rows.Count > 0) { _taskGrid.Rows[0].Selected = true; UpdateTaskDetail(); }
        }

        private void RefreshBountiesSafe()
        {
            ConfigureBountyGrid(); _refreshing = true;
            try
            {
                _taskGrid.Rows.Clear(); _selectedBountyId = -1; _selectedBountyPtr = IntPtr.Zero; _selectedBountyState = -1; _selectedQuestItem = null; _acceptButton.Text = "接取选中"; _acceptButton.Enabled = false;
                _completeButton.Text = "完成选中"; _completeButton.Enabled = false; _completeAllButton.Text = "完成全部"; _completeAllButton.Enabled = _game.IsConnected; if (_bountyProbeAButton != null) _bountyProbeAButton.Enabled = false; if (_bountyProbeBButton != null) _bountyProbeBButton.Enabled = false;
                if (!_game.IsConnected) { _detailName.Text = "悬赏"; _detailMeta.Text = "尚未连接游戏"; _detailReward.Text = ""; _objectives.Text = "连接后读取真实 BountyManager。"; return; }
                List<BountyInfo> list = _game.GetBounties(); foreach (BountyInfo b in list) { int done = b.Progress == null ? 0 : b.Progress.Count(x => x.Type != 4 && x.Remaining <= 0); int total = b.Progress == null ? 0 : b.Progress.Count(x => x.Type != 4); string prog = total == 0 ? "—" : done + " / " + total; int row = _taskGrid.Rows.Add(b.StatusLabel, b.TypeLabel, b.DisplayName, prog, b.Reward, b.Id); _taskGrid.Rows[row].Tag = b; }
                _detailName.Text = "悬赏运行时"; _detailMeta.Text = "已读取 " + list.Count + " 个运行时对象"; _detailReward.Text = "未接取可接取；进行中可设为当前追踪；完成选中使用 Progress+0x70 + 游戏线程评估队列"; _objectives.Text = "选择一条悬赏查看完整目标。";
                if (_taskGrid.Rows.Count > 0)
                {
                    int target = 0;
                    for (int i = 0; i < _taskGrid.Rows.Count; i++) { BountyInfo bi = _taskGrid.Rows[i].Tag as BountyInfo; if (bi != null && bi.IsCurrent) { target = i; break; } }
                    _taskGrid.ClearSelection(); _taskGrid.Rows[target].Selected = true; _taskGrid.CurrentCell = _taskGrid.Rows[target].Cells[0];
                }
            }
            catch (Exception ex) { Log("刷新悬赏失败：" + ex.Message); _detailMeta.Text = "刷新失败"; _objectives.Text = ex.Message; }
            finally { _refreshing = false; }
            UpdateTaskDetail();
        }

        private void UpdateTaskDetail()
        {
            if (_refreshing || _taskGrid.SelectedRows.Count == 0) return;
            if (_taskMode != "悬赏")
            {
                TaskCatalogItem item = _taskGrid.SelectedRows[0].Tag as TaskCatalogItem; if (item == null) return; _selectedQuestItem = item; _detailName.Text = item.name; _detailMeta.Text = _taskMode + " · QuestManager 运行时探针已接入"; _detailReward.Text = "内部 Key：" + item.key; _objectives.Text = "来源：" + item.source + "\r\n\r\n当前写入仍关闭。点下方“深度采集 Quest”会直接校验 gamedll+0x1D1C790 的 Quests::QuestManager 单例，扫描 +0x20、+0x38/+0x40 等任务容器、root+0xC00 与子对象，并输出 Diagnostics 文本。"; _acceptButton.Text = "接取选中"; _acceptButton.Enabled = false; _completeButton.Text = "深度采集 Quest"; _completeButton.Enabled = _game.IsConnected; _completeAllButton.Text = "Quest 总诊断"; _completeAllButton.Enabled = _game.IsConnected; if (_bountyProbeAButton != null) _bountyProbeAButton.Enabled = false; if (_bountyProbeBButton != null) _bountyProbeBButton.Enabled = false; return;
            }
            BountyInfo b = _taskGrid.SelectedRows[0].Tag as BountyInfo; if (b == null) return; _selectedQuestItem = null; _selectedBountyId = b.Id; _selectedBountyPtr = b.Ptr; _selectedBountyState = b.State;
            if (b.State == 1) { _acceptButton.Text = "接取选中"; _acceptButton.Enabled = true; }
            else if (b.State == 2 && !b.IsCurrent) { _acceptButton.Text = "设为当前追踪"; _acceptButton.Enabled = true; }
            else if (b.State == 2) { _acceptButton.Text = "当前追踪中"; _acceptButton.Enabled = false; }
            else { _acceptButton.Text = "已完成"; _acceptButton.Enabled = false; }
            _completeButton.Text = "完成选中"; _completeButton.Enabled = _game.IsConnected && b.State == 2; _completeAllButton.Text = "完成全部"; _completeAllButton.Enabled = _game.IsConnected; if (_bountyProbeAButton != null) _bountyProbeAButton.Enabled = _game.IsConnected && b.State == 2; if (_bountyProbeBButton != null) _bountyProbeBButton.Enabled = _game.IsConnected && b.State == 2; _detailName.Text = b.DisplayName; _detailMeta.Text = b.StatusLabel + " · " + b.TypeLabel + " · ID " + b.Id + (b.IsCurrent ? " · 当前追踪" : ""); _detailReward.Text = "奖励：" + (String.IsNullOrWhiteSpace(b.Reward) ? "游戏内动态奖励" : b.Reward); _objectives.Text = BuildObjectiveText(b);
        }

        private static string BuildObjectiveText(BountyInfo b)
        {
            StringBuilder sb = new StringBuilder(); List<BountyObjectiveCatalogItem> cats = b.Catalog == null ? null : b.Catalog.objectives; int runtime = b.Progress == null ? 0 : b.Progress.Count; int catalog = cats == null ? 0 : cats.Count; int count = Math.Max(runtime, catalog);
            for (int i = 0; i < count; i++)
            {
                BountyProgressInfo p = i < runtime ? b.Progress[i] : null; BountyObjectiveCatalogItem c = i < catalog ? cats[i] : null; string name = c == null || String.IsNullOrWhiteSpace(c.display) ? (c == null || String.IsNullOrWhiteSpace(c.nameToken) ? "目标 " + (i + 1) : CleanToken(c.nameToken)) : c.display; if (c != null && c.hidden) name += "（隐藏）"; bool failure = p != null ? p.Type == 4 : (c != null && String.Equals(c.kind, "FailureCount", StringComparison.OrdinalIgnoreCase));
                if (p == null) { if (failure) sb.Append("[失败条件] ").Append(name).AppendLine(); else sb.Append("○ ").Append(name).AppendLine("  [等待运行时载入]"); continue; }
                if (failure) sb.Append("[失败条件] ").Append(name).Append("：").AppendLine(p.Remaining > 0 ? "未触发" : "已触发"); else { int done = Math.Max(0, p.Total - p.Remaining); sb.Append(p.Remaining <= 0 ? "✓ " : "○ ").Append(name).Append("  ").Append(done).Append(" / ").Append(Math.Max(0, p.Total)).AppendLine(); }
            }
            if (sb.Length == 0) sb.Append("没有返回目标记录。"); return sb.ToString();
        }

        private static string CleanToken(string value) { if (String.IsNullOrWhiteSpace(value)) return ""; return value.Trim().Trim('&').Replace('_', ' '); }

        private BountyInfo SelectedSnapshot()
        {
            if (!_game.IsConnected || _selectedBountyId < 0) return null; List<BountyInfo> list = _game.GetBounties(); BountyInfo exact = _selectedBountyPtr == IntPtr.Zero ? null : list.FirstOrDefault(x => x.Ptr == _selectedBountyPtr); if (exact != null && (_selectedBountyState < 0 || exact.State == _selectedBountyState)) return exact; List<BountyInfo> same = list.Where(x => x.Id == _selectedBountyId).ToList(); if (_selectedBountyState >= 0) { List<BountyInfo> state = same.Where(x => x.State == _selectedBountyState).ToList(); if (state.Count == 1) return state[0]; } if (same.Count == 1) return same[0]; throw new InvalidOperationException("同一悬赏 ID 存在多个运行时对象，请刷新后重新选择。");
        }

        private void PrimaryBountyActionSafe()
        {
            if (_taskMode != "悬赏" || !RequireConnection()) return;
            BountyInfo b = SelectedSnapshot();
            if (b == null) return;
            if (b.State == 1)
            {
                RunBusy(delegate { _game.AcceptBounty(b.Id, b.Ptr); Log("接取悬赏：" + b.DisplayName + "。游戏已切换到进行中状态。"); RefreshBountiesSafe(); });
                return;
            }
            if (b.State == 2)
            {
                RunBusy(delegate { _game.SetActiveBounty(b.Id, b.Ptr); Log("设为当前追踪悬赏：" + b.DisplayName + "。"); RefreshBountiesSafe(); });
                return;
            }
            MessageBox.Show(this, "当前悬赏已经是完成状态，不能再执行接取/追踪操作。", "状态不匹配", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private string DiagnosticsDirectory()
        {
            string dir = Path.Combine(_rootDir, "Diagnostics");
            Directory.CreateDirectory(dir);
            return dir;
        }

        private void ApplyRunFactorSafe()
        {
            if (!RequireConnection()) return;
            int factor = _runPicker == null ? 1 : _runPicker.Value;
            RunBusy(delegate { string result = _game.SetRunSpeedFactor(factor); Log("奔跑速度 → x" + factor + "；" + result); if (_runHitStatus != null) _runHitStatus.Text = "x" + factor + " 已应用。先在游戏内实际移动/冲刺，然后点“检查命中”。"; });
        }

        private void ApplyJumpFactorSafe()
        {
            if (!RequireConnection()) return;
            int factor = _jumpPicker == null ? 1 : _jumpPicker.Value;
            RunBusy(delegate { string result = _game.SetJumpHeightFactor(factor); Log("跳跃高度 → x" + factor + "；" + result); });
        }

        private void SecondaryTaskActionSafe(bool second)
        {
            if (_taskMode == "悬赏")
            {
                if (second)
                {
                    if (!RequireConnection()) return;
                    if (MessageBox.Show(this, "将依次完成当前所有进行中的悬赏。每条都会先把目标置 0，再送入 BountyManager 原生游戏线程评估队列；任一条失败立即停止。继续吗？", "完成全部已接", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                    RunBusy(delegate
                    {
                        List<BountyInfo> active = _game.GetBounties().Where(x => x.State == 2).ToList();
                        int ok = 0;
                        foreach (BountyInfo x in active)
                        {
                            try { _game.CompleteBountyStaged(x.Id, x.Ptr, Log); ok++; }
                            catch (Exception ex) { Log("批量完成在 " + x.DisplayName + " 停止：" + ex.Message); break; }
                            System.Threading.Thread.Sleep(140);
                        }
                        Log("完成全部已接结束：" + ok + "/" + active.Count + "。 "); RefreshBountiesSafe();
                    });
                    return;
                }
                if (!RequireConnection()) return;
                BountyInfo b = SelectedSnapshot();
                if (b == null || b.State != 2) { MessageBox.Show(this, "请选择一条进行中的悬赏。", "状态不匹配", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
                if (MessageBox.Show(this,
                    "将把当前悬赏的普通目标 remaining 置 0，并追加到 BountyManager+0x6F0 原生评估队列。Evaluate/状态推进由游戏线程完成，不再从 CreateRemoteThread 调任务函数。\r\n\r\n继续完成 “" + b.DisplayName + "” 吗？",
                    "完成选中", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                RunBusy(delegate
                {
                    try
                    {
                        int count = _game.CompleteBountyStaged(b.Id, b.Ptr, Log);
                        Log("完成选中结束：" + b.DisplayName + "，处理 " + count + " 个目标记录并交给游戏线程评估。");
                        RefreshBountiesSafe();
                    }
                    catch (Exception ex)
                    {
                        try
                        {
                            string diag = _game.CaptureBountyFailureDiagnostic(b.Id, b.Ptr, ex.Message);
                            string safe = (b.DisplayName ?? ("Bounty_" + b.Id)).Replace(' ', '_');
                            foreach (char c in Path.GetInvalidFileNameChars()) safe = safe.Replace(c, '_');
                            if (safe.Length > 60) safe = safe.Substring(0, 60);
                            string path = Path.Combine(DiagnosticsDirectory(), "BountyFailure_" + b.Id + "_" + safe + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt");
                            File.WriteAllText(path, diag, Encoding.UTF8);
                            Log("悬赏失败诊断已自动导出：" + path);
                        }
                        catch (Exception dex) { Log("悬赏失败诊断导出也失败：" + dex.Message); }
                        throw;
                    }
                });
                return;
            }
            if (second) ExportQuestOverviewProbeSafe();
            else ExportSelectedQuestProbeSafe();
        }

        private void ExportSelectedBountyProbeSafe(bool compare)
        {
            if (_taskMode != "悬赏" || !RequireConnection()) return;
            BountyInfo b = SelectedSnapshot();
            if (b == null || b.State != 2)
            {
                MessageBox.Show(this, "请选择一条进行中的悬赏。", "赏金采样", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            RunBusy(delegate
            {
                string text = _game.CaptureBountyProgressProbe(b.Id, b.Ptr, compare);
                if (!compare)
                {
                    Log("赏金采样 A：" + b.DisplayName + "。现在只正常推进 1 个最小目标单位，然后立即点赏金采样 B。");
                    MessageBox.Show(this, "A 基线已保存。\r\n\r\n现在回到游戏，只让这条悬赏的一个目标正常增加/减少 1 个最小单位（例如击杀 1 只、完成 1 次动作），不要切任务、不要领取奖励，然后立即点“赏金采样 B”。", "赏金采样 A", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    string safe = (b.DisplayName ?? ("Bounty_" + b.Id)).Replace(' ', '_');
                    foreach (char c in Path.GetInvalidFileNameChars()) safe = safe.Replace(c, '_');
                    if (safe.Length > 60) safe = safe.Substring(0, 60);
                    string path = Path.Combine(DiagnosticsDirectory(), "BountyProbe_" + b.Id + "_" + safe + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt");
                    File.WriteAllText(path, text, Encoding.UTF8);
                    Log("赏金采样 B 已导出：" + path);
                    MessageBox.Show(this, "已导出：\r\n" + path + "\r\n\r\n把这个 txt 发给我即可继续定位真正的悬赏进度源。", "赏金采样完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            });
        }

        private void ExportSelectedQuestProbeSafe()
        {
            if (_taskMode == "悬赏" || _selectedQuestItem == null || !RequireConnection()) return;
            RunBusy(delegate
            {
                string text = _game.BuildQuestRuntimeProbe(_taskMode, _selectedQuestItem.name, _selectedQuestItem.key);
                string safe = String.Join("_", (_selectedQuestItem.key ?? "quest").Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
                if (safe.Length > 80) safe = safe.Substring(0, 80);
                string path = Path.Combine(DiagnosticsDirectory(), "QuestProbe_" + _taskMode + "_" + safe + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt");
                File.WriteAllText(path, text, Encoding.UTF8);
                Log("Quest 运行时诊断已导出：" + path);
                MessageBox.Show(this, "已导出：\r\n" + path + "\r\n\r\n把这个 txt 发给我即可继续定位 QuestManager。", "诊断完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            });
        }

        private void ExportQuestOverviewProbeSafe()
        {
            if (!RequireConnection()) return;
            RunBusy(delegate
            {
                string text = _game.BuildQuestOverviewProbe();
                string path = Path.Combine(DiagnosticsDirectory(), "QuestOverview_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt");
                File.WriteAllText(path, text, Encoding.UTF8);
                Log("Quest 总诊断已导出：" + path);
                MessageBox.Show(this, "已导出：\r\n" + path, "诊断完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            });
        }

        private CollectibleCategory SelectedCollectibleCategory()
        {
            if (_collectibleGrid == null || _collectibleGrid.SelectedRows.Count == 0) return null;
            return _collectibleGrid.SelectedRows[0].Tag as CollectibleCategory;
        }

        private void RefreshCollectiblesSafe()
        {
            if (_collectibleGrid == null) return;
            try
            {
                string selectedKey = null;
                CollectibleCategory selectedBefore = SelectedCollectibleCategory();
                if (selectedBefore != null) selectedKey = selectedBefore.key;
                _collectibleGrid.Rows.Clear();
                if (!_game.IsConnected)
                {
                    _collectibleName.Text = "收集品"; _collectibleMeta.Text = "尚未连接游戏"; _collectibleDetail.Text = "启动游戏并进入存档后会自动连接；也可以点击右上角“连接游戏”。";
                    _collectibleOneButton.Enabled = false; _collectibleAllButton.Enabled = false; _collectibleAllSupportedButton.Enabled = false;
                    if (_collectibleSnapshotButton != null) _collectibleSnapshotButton.Enabled = false;
                    return;
                }

                _collectedIds = _game.GetCollectedIds();
                // v0.1.0's BaseItem memory index was not a verified collected-ID.
                // Only the read-back proven A/B observations are used in v0.1.2.
                foreach (CollectibleCategory c in _collectibleCategories)
                {
                    if (String.Equals(c.key, "following_missing_persons", StringComparison.OrdinalIgnoreCase) ||
                        String.Equals(c.key, "following_strange_rocks", StringComparison.OrdinalIgnoreCase))
                    {
                        // Only use read-back proven A/B ID differences. An IMIG
                        // BaseItem memory index is NOT a Collectable ID.
                        List<CollectibleItem> observed;
                        c.items = _v011Observed.TryGetValue(c.key, out observed)
                            ? observed.OrderBy(x => x.id).ToList() : new List<CollectibleItem>();
                        c.supported = false;
                    }
                }
                _blueprintIds = _game.GetBlueprintInternalIds();
                _blueprintRuntime = _game.GetBlueprintRuntimeV012(_collectedIds);

                int selectRow = -1;
                foreach (CollectibleCategory c in _collectibleCategories)
                {
                    List<CollectibleItem> items = c.items ?? new List<CollectibleItem>();
                    bool blueprint = String.Equals(c.key, "following_blueprints", StringComparison.OrdinalIgnoreCase);
                    int done = items.Count(x => _collectedIds.Contains(x.id));
                    int total = items.Count;
                    bool special = String.Equals(c.key, "following_missing_persons", StringComparison.OrdinalIgnoreCase) ||
                        String.Equals(c.key, "following_strange_rocks", StringComparison.OrdinalIgnoreCase);
                    // An unknown ownership map is NOT a zero-progress save. The user may have
                    // already completed all 9 people / 15 rocks, so never display 0 / N here.
                    string progress = blueprint ? ("标记 " + _blueprintRuntime.Count(x => x.InCollectedList) + " / " + _blueprintIds.Count)
                        : special ? ("? / " + c.plannedCount)
                        : (total > 0 ? done + " / " + total : "—");
                    string state;
                    if (blueprint) state = _blueprintIds.Count > 0 ? "可验证 / 可尝试补齐" : "未解析";
                    else if (special) state = items.Count > 0 ? ("核实ID " + done) : "持久状态未知";
                    else if (c.supported) state = done >= total && total > 0 ? "已齐全" : "可补齐";
                    else state = "只读";
                    int row = _collectibleGrid.Rows.Add(c.scope ?? "", c.title ?? c.key ?? "", progress, state);
                    _collectibleGrid.Rows[row].Tag = c;
                    if (!String.IsNullOrEmpty(selectedKey) && String.Equals(selectedKey, c.key, StringComparison.OrdinalIgnoreCase)) selectRow = row;
                }
                _collectibleAllSupportedButton.Enabled = true;
                if (_collectibleSnapshotButton != null) _collectibleSnapshotButton.Enabled = true;
                if (_collectibleGrid.Rows.Count > 0)
                {
                    if (selectRow < 0) selectRow = 0;
                    _collectibleGrid.Rows[selectRow].Selected = true;
                    try { _collectibleGrid.FirstDisplayedScrollingRowIndex = Math.Max(0, selectRow - 2); } catch { }
                }
                UpdateCollectibleDetail();
                int supportedTotal = _collectibleCategories.Where(x => x.supported).Sum(x => (x.items ?? new List<CollectibleItem>()).Count);
                int supportedDone = _collectibleCategories.Where(x => x.supported).Sum(x => (x.items ?? new List<CollectibleItem>()).Count(i => _collectedIds.Contains(i.id)));
                Log("收集品进度刷新：游戏 collected-ID=" + _collectedIds.Count + "；可精确读写=" + supportedDone + "/" + supportedTotal + "；Craftplan 目录=" + _blueprintIds.Count + "。 ");
            }
            catch (Exception ex) { Log("刷新收集品失败：" + ex.Message); }
        }

        private void UpdateCollectibleDetail()
        {
            if (_collectibleGrid == null || _collectibleName == null) return;
            CollectibleCategory c = SelectedCollectibleCategory();
            if (c == null)
            {
                _collectibleName.Text = "请选择分类"; _collectibleMeta.Text = ""; _collectibleDetail.Text = ""; _collectibleOneButton.Enabled = false; _collectibleAllButton.Enabled = false; return;
            }
            bool blueprint = String.Equals(c.key, "following_blueprints", StringComparison.OrdinalIgnoreCase);
            List<CollectibleItem> items = c.items ?? new List<CollectibleItem>();
            List<CollectibleItem> missing = items.Where(x => !_collectedIds.Contains(x.id)).ToList();
            int done = items.Count - missing.Count;
            _collectibleName.Text = (c.scope ?? "") + " · " + (c.title ?? c.key ?? "");
            if (blueprint)
                _collectibleMeta.Text = "目录 " + _blueprintIds.Count + " 项，已有 collected-ID 标记 " + _blueprintRuntime.Count(x => x.InCollectedList) +
                    "；✓=存档中已有标记，?=状态未核实（不能当缺失）";
            else
                _collectibleMeta.Text = c.supported
                    ? ("实时收集进度 " + done + "/" + items.Count + " · 原生 collected-ID 读取")
                    : ("真实已收集数未知 · 已验证 " + items.Count + " 个 ID" + (c.plannedCount > 0 ? " / 预期 " + c.plannedCount : "") + " · 只读");

            StringBuilder sb = new StringBuilder();
            if (!String.IsNullOrWhiteSpace(c.description)) sb.AppendLine(c.description).AppendLine();
            if (blueprint)
            {
                if (_blueprintIds.Count == 0) sb.Append("当前 BaseItem 表尚未解析到 Craftplan_*。完全进入存档并打开一次背包/制作界面后刷新。");
                else
                {
                    sb.AppendLine("游戏简体中文名 + 内部 ID（✓=检测到存档 collected-ID 标记；?=未检测到，仍可能通过剧情/技能解锁）：");
                    Dictionary<string, BlueprintRuntimeRecord> byId = _blueprintRuntime.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
                    foreach (string id in _blueprintIds)
                    {
                        BlueprintRuntimeRecord rec;
                        string status = byId.TryGetValue(id, out rec) && rec.InCollectedList ? "✓" : "?";
                        sb.AppendLine(status + "  " + BlueprintChineseV012(id) + "  [" + id + "]");
                    }
                }
            }
            else if (items.Count == 0)
            {
                if (c.plannedCount > 0) sb.Append("预期 " + c.plannedCount + " 项。当前版本尚未定位该类别的持久状态表，因此不能凭现有 collected-ID 总表判断是否完成。" +
                    Environment.NewLine + "你已在游戏中收集齐全时，不需要再尝试‘拾取1项’的 A/B 采样。可以直接点击下方‘导出当前存档快照’，提供只读现状进行后续定位；不会把未知错误显示成 ×。");
                else sb.Append("该分类当前没有可读取条目。");
            }
            else
            {
                sb.AppendLine("详细清单（✓ 当前存档已收集 / × 未收集；特殊分类仅列 A/B 观测 ID）：");
                foreach (CollectibleItem i in items)
                    sb.AppendLine((_collectedIds.Contains(i.id) ? "✓  " : "×  ") + i.id + " · " + (i.name ?? ""));
            }
            _collectibleDetail.Text = sb.ToString();
            bool probe = IsV011SpecialProbeCategory(c.key);
            if (probe) sb.AppendLine().AppendLine("【存档已收齐的情况】先用页面底部‘导出当前存档快照’，不用找一件尚未领取的物品来做 A/B。若游戏还有新的获取动作，才使用 A/B；蓝图状态未验证时显示 ?，不是 ×。");
            if (probe) _collectibleDetail.Text = sb.ToString();
            _collectibleOneButton.Text = blueprint ? "尝试补 1 蓝图" : (probe ? "采样 A" : "补 1 项");
            _collectibleOneButton.Enabled = blueprint ? (_game.IsConnected && _blueprintIds.Count > 0) : (probe ? _game.IsConnected : (c.supported && missing.Count > 0));
            _collectibleAllButton.Text = blueprint ? "尝试补齐蓝图" : (probe ? "采样 B" : "补齐分类");
            _collectibleAllButton.Enabled = blueprint ? (_game.IsConnected && _blueprintIds.Count > 0) :
                (probe ? (_game.IsConnected && _v011SampleCollectedIds != null && _v011SampleKind == c.key) : (c.supported && missing.Count > 0));
        }

        private void CompleteOneCollectibleSafe()
        {
            if (!RequireConnection()) return;
            CollectibleCategory c = SelectedCollectibleCategory(); if (c == null) return;
            if (String.Equals(c.key, "following_blueprints", StringComparison.OrdinalIgnoreCase)) { UnlockBlueprintsV012(false); return; }
            if (IsV011SpecialProbeCategory(c.key)) { CollectibleProbeV011(false); return; }
            if (!c.supported) return;
            List<CollectibleItem> missing = (c.items ?? new List<CollectibleItem>()).Where(x => !_collectedIds.Contains(x.id)).ToList();
            if (missing.Count == 0) return;
            CollectibleItem item = missing[0];
            RunBusy(delegate { _game.AwardCollectable(item.id); Log("补收集品 1 项 → " + c.scope + " / " + c.title + " · " + item.id + " " + item.name); RefreshCollectiblesSafe(); });
        }

        private static bool IsV011SpecialProbeCategory(string key)
        {
            return String.Equals(key, "following_blueprints", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(key, "following_missing_persons", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(key, "following_strange_rocks", StringComparison.OrdinalIgnoreCase);
        }

        private string V011ObservationPath()
        {
            return Path.Combine(_rootDir, "Data", "ObservedCollectibleIds.json");
        }

        private void LoadObservedCollectiblesV011()
        {
            string path = V011ObservationPath();
            if (!File.Exists(path)) return;
            try
            {
                JavaScriptSerializer json = new JavaScriptSerializer(); json.MaxJsonLength = int.MaxValue;
                Dictionary<string, List<CollectibleItem>> record = json.Deserialize<Dictionary<string, List<CollectibleItem>>>(File.ReadAllText(path, Encoding.UTF8));
                if (record != null)
                    foreach (KeyValuePair<string, List<CollectibleItem>> pair in record)
                        if (IsV011SpecialProbeCategory(pair.Key) && pair.Value != null)
                            _v011Observed[pair.Key] = pair.Value.Where(x => x.id > 0).GroupBy(x => x.id).Select(x => x.First()).ToList();
            }
            catch { } // corrupted user observation file must not affect a game save
        }

        private void SaveObservedCollectiblesV011()
        {
            JavaScriptSerializer json = new JavaScriptSerializer(); json.MaxJsonLength = int.MaxValue;
            string path = V011ObservationPath();
            File.WriteAllText(path, json.Serialize(_v011Observed), new UTF8Encoding(false));
        }

        private void CollectibleProbeV011(bool compare)
        {
            if (!RequireConnection()) return;
            CollectibleCategory c = SelectedCollectibleCategory();
            if (c == null || !IsV011SpecialProbeCategory(c.key)) return;
            RunBusy(delegate
            {
                if (!compare)
                {
                    _v011SampleCollectedIds = _game.GetCollectedIds();
                    _v011SampleKind = c.key;
                    _game.CaptureFeatureProbe("武器 / 库存", false);
                    Log("A/B 基线已采样 [" + c.title + "]；collected-ID 数=" + _v011SampleCollectedIds.Count + "。现在在游戏里正常获取一项，再点 B。");
                    UpdateCollectibleDetail();
                    return;
                }
                if (_v011SampleCollectedIds == null || _v011SampleKind != c.key)
                    throw new InvalidOperationException("必须在同一分类先采样 A，再采样 B。");
                HashSet<int> now = _game.GetCollectedIds();
                List<int> added = now.Except(_v011SampleCollectedIds).OrderBy(x => x).ToList();
                List<int> removed = _v011SampleCollectedIds.Except(now).OrderBy(x => x).ToList();
                string raw = _game.CaptureFeatureProbe("武器 / 库存", true);
                StringBuilder report = new StringBuilder();
                report.AppendLine("DL1 Assistant 0.1.1 / " + c.title + " / A-B");
                report.AppendLine("同一存档同一类别：获取 1 件真实物品或学习 1 张蓝图");
                report.AppendLine("Before=" + _v011SampleCollectedIds.Count + " After=" + now.Count);
                report.AppendLine("Added collected-IDs: " + (added.Count == 0 ? "(none)" : String.Join(", ", added)));
                report.AppendLine("Removed collected-IDs: " + (removed.Count == 0 ? "(none)" : String.Join(", ", removed)));
                report.AppendLine("Note: a CraftPlan may be stored outside Collectable; don't interpret no delta as not learned.");
                report.AppendLine().Append(raw);
                string folder = DiagnosticsDirectory();
                string tag = c.key.Replace("/", "_").Replace(" ", "_");
                string path = Path.Combine(folder, "CollectibleAB_" + tag + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt");
                File.WriteAllText(path, report.ToString(), Encoding.UTF8);
                // Only map exactly one new Collectable ID to the specific category when the
                // player says they normally picked one item. Never turn this into a write ABI.
                if (!String.Equals(c.key, "following_blueprints", StringComparison.OrdinalIgnoreCase) && added.Count == 1 && removed.Count == 0)
                {
                    List<CollectibleItem> items;
                    if (!_v011Observed.TryGetValue(c.key, out items)) { items = new List<CollectibleItem>(); _v011Observed[c.key] = items; }
                    if (!items.Any(x => x.id == added[0]))
                    {
                        items.Add(new CollectibleItem { id = added[0], name = "A/B 实测 " + c.title + " 收集 ID " + added[0] });
                        SaveObservedCollectiblesV011();
                    }
                }
                _v011SampleCollectedIds = null;
                _v011SampleKind = null;
                Log("A/B 已导出 " + c.title + "：增加 " + added.Count + " 个 collected-ID；文件：" + path);
                RefreshCollectiblesSafe();
                MessageBox.Show(this, "已生成 A/B 诊断：\r\n" + path + "\r\n\r\n新增 collected-ID：" +
                    (added.Count == 0 ? "无（可能是任务状态 / 已学 CraftPlan 独立容器）" : String.Join(", ", added)) +
                    "\r\n把报告发给我，可进一步精确定位原生状态与补齐方式。", "A/B 收集品诊断", MessageBoxButtons.OK, MessageBoxIcon.Information);
            });
        }

        // A one-click read-only snapshot is essential for saves where the 9 Missing
        // Persons and 15 Strange Rocks have ALREADY been found: there is no new
        // pickup left to trigger a meaningful A/B delta. A BaseItem definition is
        // not evidence of ownership; do not mark unknown CraftPlans as unlearned.
        private void ExportCollectedSnapshotSafe()
        {
            if (!RequireConnection()) return;
            RunBusy(delegate
            {
                HashSet<int> collected = _game.GetCollectedIds();
                List<string> blueprintDirectory = _game.GetBlueprintInternalIds();
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("DL1 Task Assistant v0.1.2 - 只读收集品 / CraftPlan 当前存档快照");
                sb.AppendLine("时间=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine("目标 gamedll SHA-256=" + GameClient.ExpectedHash);
                sb.AppendLine("Collected-ID 总计=" + collected.Count);
                sb.AppendLine("Craftplan_* 已加载定义=" + blueprintDirectory.Count);
                sb.AppendLine("备注：下述 CraftPlan 是物品定义目录，不是已学蓝图列表；状态 ? 不等于未学。");
                sb.AppendLine("当当前存档已经收齐失踪人口/奇怪岩石时，不能用“无新增项”的 A/B 差异否认既有进度。");
                sb.AppendLine();
                sb.AppendLine("[分类进度：仅原生 collected-ID 有对应静态 ID 的分类可自动判定]");
                foreach (CollectibleCategory category in _collectibleCategories)
                {
                    bool special = IsV011SpecialProbeCategory(category.key);
                    List<CollectibleItem> items = category.items ?? new List<CollectibleItem>();
                    if (special)
                    {
                        sb.AppendLine((category.title ?? category.key) + " : 真实进度=? / " +
                            (category.plannedCount > 0 ? category.plannedCount.ToString() : "未知总数") +
                            ", 可明确映射 ID=" + items.Count + "; 保存中并非 0 / N。");
                        continue;
                    }
                    int have = items.Count(x => collected.Contains(x.id));
                    sb.AppendLine((category.title ?? category.key) + " : " + have + "/" + items.Count +
                        (category.supported ? " [原生 collected-ID]" : " [只读/未验证]"));
                    if (category.supported)
                        foreach (CollectibleItem item in items)
                            sb.AppendLine("  " + (collected.Contains(item.id) ? "✓ " : "× ") + item.id + " " + (item.name ?? ""));
                }
                sb.AppendLine();
                sb.AppendLine("[完整已收集 collected-ID 数据：仅供寻找缺失类别的持久结构]");
                foreach (int id in collected.OrderBy(x => x)) sb.AppendLine(id.ToString());
                sb.AppendLine();
                sb.AppendLine("[运行时 Craftplan_* 目录：仅 collected-ID 容器中确认的标记显示 ✓，其余未知]");
                Dictionary<string, BlueprintRuntimeRecord> runtimeMap = _game.GetBlueprintRuntimeV012(collected)
                    .ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
                foreach (string id in blueprintDirectory)
                {
                    BlueprintRuntimeRecord record;
                    bool marker = runtimeMap.TryGetValue(id, out record) && record.InCollectedList;
                    sb.AppendLine((marker ? "✓ " : "? ") + BlueprintChineseV012(id) + " [" + id + "]");
                }
                sb.AppendLine();
                sb.AppendLine("[CT 交叉核验]");
                sb.AppendLine("XXXeLxxx CT 含玩家/计时/战斗 Hook，未含失踪人口、奇怪岩石或已学 CraftPlan 状态表。");
                sb.AppendLine("IMIG -4.5P1 CT 的 BaseItem/背包编辑器提供物品定义及 +0x58 内存索引，未提供学习进度容器。");
                sb.AppendLine("CraftplanTaken / SEventCraftPlanObtained 属于游戏二进制里的事件标识，不能直接当作已学列表。");
                sb.AppendLine("全程只读，不修改收集状态、武器、物品及配方。");
                string folder = DiagnosticsDirectory();
                string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string full = Path.Combine(folder, "CollectionSnapshot_" + stamp + ".txt");
                File.WriteAllText(full, sb.ToString(), new UTF8Encoding(true));
                // Tab-delimited Excel-friendly inventory for sorting/filtering all
                // Craftplan_* names. 'Unknown' is not a false negative.
                string table = Path.Combine(folder, "CraftPlanDirectory_" + stamp + ".tsv");
                StringBuilder lines = new StringBuilder();
                lines.AppendLine("InternalId	OfficialChineseName	CollectedMarker	Source");
                foreach (string id in blueprintDirectory)
                {
                    BlueprintRuntimeRecord record;
                    bool marker = runtimeMap.TryGetValue(id, out record) && record.InCollectedList;
                    lines.AppendLine(id + "	" + BlueprintChineseV012(id).Replace("	", " ") +
                        "	" + (marker ? "InCollectedList" : "Unknown") + "	Data0+DataCn/IMIG");
                }
                File.WriteAllText(table, lines.ToString(), new UTF8Encoding(true));
                Log("只读收集品快照：" + full + "；蓝图目录：" + table + "。Collected=" + collected.Count + ", Blueprint definitions=" + blueprintDirectory.Count);
                MessageBox.Show(this, "已导出当前存档只读快照：\r\n" + full + "\r\n\r\n蓝图目录：\r\n" + table +
                    "\r\n\r\n包含从 DataCn.pak 提取的游戏中文名称。未核实蓝图状态仍为 Unknown，不会错误标记成未学习。",
                    "当前存档快照", MessageBoxButtons.OK, MessageBoxIcon.Information);
            });
        }

        private void ExportBlueprintProbeSafe()
        {
            if (!RequireConnection()) return;
            RunBusy(delegate
            {
                List<string> ids = _game.GetBlueprintInternalIds();
                _blueprintIds = ids;
                StringBuilder sb = new StringBuilder(); sb.AppendLine("当前 1.55 BaseItem 表 Craftplan_*：" + ids.Count);
                foreach (string id in ids) sb.AppendLine("?  " + BlueprintChineseV012(id) + "  [" + id + "]");
                if (_collectibleDetail != null) _collectibleDetail.Text = sb.ToString();
                Log("蓝图目录刷新：解析到 " + ids.Count + " 个 Craftplan_* BaseItem Definition。");
            });
        }

        private void CompleteCollectibleCategorySafe()
        {
            if (!RequireConnection()) return;
            CollectibleCategory c = SelectedCollectibleCategory(); if (c == null) return;
            if (String.Equals(c.key, "following_blueprints", StringComparison.OrdinalIgnoreCase)) { UnlockBlueprintsV012(true); return; }
            if (IsV011SpecialProbeCategory(c.key)) { CollectibleProbeV011(true); return; }
            if (!c.supported) return;
            List<CollectibleItem> missing = (c.items ?? new List<CollectibleItem>()).Where(x => !_collectedIds.Contains(x.id)).ToList();
            if (missing.Count == 0) return;
            if (MessageBox.Show(this, "补齐 “" + c.scope + " / " + c.title + "” 缺少的 " + missing.Count + " 项？", "补齐收集品", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            AwardCollectibles(c.scope + " / " + c.title, missing);
        }

        private void UnlockBlueprintsV012(bool all)
        {
            if (!RequireConnection()) return;
            List<string> missing = _blueprintRuntime.Where(x => !x.InCollectedList).Select(x => x.Id).ToList();
            if (missing.Count == 0) { Log("当前蓝图目录均有 collected-ID 标记；不重复写入。"); return; }
            if (!all) missing = missing.Take(1).ToList();
            if (MessageBox.Show(this, "按当前 1.55 游戏原生 FindCollectableObject → AwardCollectable 尝试 " +
                missing.Count + " 个 Craftplan。每个 ID 都会经游戏管理器检查，读回未确认绝不报告成功。" +
                Environment.NewLine + "部分蓝图可能是技能/DLC授予，未注册则跳过。先备份存档。继续？",
                "补充蓝图", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            RunBusy(delegate
            {
                int success = 0, skipped = 0;
                foreach (string id in missing)
                {
                    try
                    {
                        string info = _game.TryNativeUnlockBlueprintV012(id);
                        if (info.StartsWith("已写入", StringComparison.Ordinal)) success++; else skipped++;
                        Log(info);
                    }
                    catch (Exception ex) { skipped++; Log("蓝图 " + id + " 跳过：" + ex.Message); }
                    if ((success + skipped) % 12 == 0) Application.DoEvents();
                }
                Log("蓝图原生尝试结束：读回新标记 " + success + "，未确认/跳过 " + skipped + "。");
                RefreshCollectiblesSafe();
            });
        }

        private void CompleteAllCollectiblesSafe()
        {
            if (!RequireConnection()) return;
            List<CollectibleItem> missing = new List<CollectibleItem>();
            foreach (CollectibleCategory c in _collectibleCategories.Where(x => x.supported))
                foreach (CollectibleItem item in c.items ?? new List<CollectibleItem>()) if (!_collectedIds.Contains(item.id)) missing.Add(item);
            missing = missing.GroupBy(x => x.id).Select(x => x.First()).ToList();
            if (missing.Count == 0) { Log("全部已支持收集品已经齐全。"); return; }
            if (MessageBox.Show(this, "补齐全部已支持分类中缺少的 " + missing.Count + " 项？\r\n只会修改已精确接入/精确解析的 collected-ID 分类；蓝图与未解析特殊分类不会混入批量写入。", "补齐全部收集品", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            AwardCollectibles("全部已支持收集品", missing);
        }

        private void AwardCollectibles(string title, List<CollectibleItem> missing)
        {
            RunBusy(delegate
            {
                Log("开始补齐 " + title + "：" + missing.Count + " 项。");
                int attempted = 0;
                foreach (CollectibleItem item in missing)
                {
                    try { _game.AwardCollectable(item.id); attempted++; }
                    catch (Exception ex) { Log("收集品 ID " + item.id + " 失败：" + ex.Message); break; }
                    if ((attempted % 12) == 0) Application.DoEvents();
                }
                System.Threading.Thread.Sleep(100);
                RefreshCollectiblesSafe();
                int verified = missing.Count(x => _collectedIds.Contains(x.id));
                Log("补齐结束：请求 " + attempted + "，游戏读回确认 " + verified + "/" + missing.Count + "。");
            });
        }

        private void RefreshWeaponPicker()
        {
            if (_weaponPicker == null || _weaponCategory == null) return; string category = _weaponCategory.SelectedItem as string; _weaponPicker.Items.Clear(); foreach (WeaponCatalogItem w in _weapons.Where(x => String.Equals(x.category ?? "其他", category, StringComparison.OrdinalIgnoreCase))) _weaponPicker.Items.Add(w); if (_weaponPicker.Items.Count > 0) _weaponPicker.SelectedIndex = 0;
        }

        private void GiveSelectedWeaponSafe()
        {
            if (!RequireConnection()) return;
            WeaponCatalogItem w = _weaponPicker == null ? null : _weaponPicker.SelectedItem as WeaponCatalogItem;
            if (w == null) return;
            RunBusy(delegate { string r = _game.GiveWeapon(w.internalId); Log("添加武器 → " + w.name + "；" + r); if (_weaponDetail != null) _weaponDetail.Text = "已调用：" + r; });
        }

        private void AddHellraidCoinsSafe(int amount)
        {
            if (!RequireConnection()) return;
            RunBusy(delegate { string r = _game.AddHellraidCoins(amount); Log("Hellraid Coins +" + amount.ToString("N0") + "；" + r); });
        }

        private void RefreshHellraidBountiesSafe()
        {
            if (_hellraidGrid == null) return; _hellraidGrid.Rows.Clear();
            if (!_game.IsConnected) { if (_hellraidStatus != null) _hellraidStatus.Text = "连接游戏后可读取 Hellraid 专属悬赏，并使用 Coins / Easy Craft / 附近敌人屠杀。"; return; }
            try
            {
                List<BountyInfo> list = _game.GetBounties().Where(x => _hellraidBountyIds.Contains(x.Id) || (!String.IsNullOrWhiteSpace(x.Source) && x.Source.IndexOf("dlc49", StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
                foreach (BountyInfo b in list) { int done = b.Progress == null ? 0 : b.Progress.Count(x => x.Type != 4 && x.Remaining <= 0); int total = b.Progress == null ? 0 : b.Progress.Count(x => x.Type != 4); int row = _hellraidGrid.Rows.Add(b.StatusLabel, b.DisplayName, total == 0 ? "—" : done + " / " + total, b.Reward, b.Id); _hellraidGrid.Rows[row].Tag = b; }
                if (_hellraidStatus != null) _hellraidStatus.Text = "已识别 " + list.Count + " 个当前运行时 Hellraid 悬赏对象。附近敌人屠杀使用 XXXeLxxx 1.55 的 35m 敌人生命消费点，不依赖 Wand 的僵尸类型过滤；请优先在普通骷髅上验证覆盖范围。";
            }
            catch (Exception ex) { Log("刷新 Hellraid 悬赏失败：" + ex.Message); }
        }

        private void RunBusy(Action action)
        {
            if (_busy) return; Cursor old = Cursor; _busy = true; try { Cursor = Cursors.WaitCursor; action(); } catch (Exception ex) { Log("操作失败：" + ex.Message); MessageBox.Show(this, ex.Message, "操作失败", MessageBoxButtons.OK, MessageBoxIcon.Warning); } finally { Cursor = old; _busy = false; }
        }

        private void Log(string text) { string line = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + text; if (_logBox != null) { _logBox.AppendText(line + Environment.NewLine); _logBox.SelectionStart = _logBox.TextLength; _logBox.ScrollToCaret(); } }
        private void OnFormClosing(object sender, FormClosingEventArgs e) { if (_closing) return; _closing = true; try { _game.Disconnect(); } catch { } }
    }
}
