using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace DL1Assistant
{
    internal sealed partial class MainForm
    {
        private DataGridView _invGrid;
        private Label _invStatus;
        private TextBox _invSearch;
        private NumericUpDown _invAmount;
        private ComboBox _invQuality;
        private NumericUpDown _invDurability;
        private NumericUpDown _invRepairs;
        private NumericUpDown _invDamage;
        private CheckBox _invChangeDamage;
        private List<RuntimeInventoryItem> _invSnapshot = new List<RuntimeInventoryItem>();

        private Panel BuildInventoryPageV012()
        {
            Panel page = NewPage();
            TableLayoutPanel root = new TableLayoutPanel(); root.Dock = DockStyle.Fill; root.Margin = Padding.Empty;
            root.ColumnCount = 1; root.RowCount = 4; root.BackColor = Theme.Background;
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 49));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 232));
            page.Controls.Add(root);
            root.Controls.Add(PageTitle("背包 / 库存", "IMIG 1.55 背包结构：武器、材料、数量、橙色品质、耐久、修理次数、同型号基础伤害"), 0, 0);

            TableLayoutPanel tools = new TableLayoutPanel(); tools.Dock = DockStyle.Fill; tools.Margin = Padding.Empty;
            tools.ColumnCount = 3; tools.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tools.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 134)); tools.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132));
            _invSearch = new TextBox(); _invSearch.Dock = DockStyle.Fill; _invSearch.Font = Theme.Normal;
            _invSearch.Margin = new Padding(0, 10, 8, 7); _invSearch.BackColor = Theme.Card;
            _invSearch.ForeColor = Theme.Text; _invSearch.BorderStyle = BorderStyle.FixedSingle;
            _invSearch.TextChanged += delegate { ShowInventorySnapshotV012(); };
            tools.Controls.Add(_invSearch, 0, 0);
            RoundedButton capture = ActionButton("初始化捕获", 120, false); capture.Dock = DockStyle.Fill;
            capture.Margin = new Padding(0, 5, 6, 5); capture.Click += delegate
            {
                if (!RequireConnection()) return;
                RunBusy(delegate { _game.EnsureInventoryCaptureV012(); Log("背包捕获已就绪：请回到游戏打开一次背包，再点刷新库存。"); });
            };
            tools.Controls.Add(capture, 1, 0);
            RoundedButton refresh = ActionButton("刷新库存", 120, true); refresh.Dock = DockStyle.Fill;
            refresh.Margin = new Padding(4, 5, 0, 5); refresh.Click += delegate { RefreshInventoryV012(); };
            tools.Controls.Add(refresh, 2, 0);
            root.Controls.Add(tools, 0, 1);

            _invGrid = new DataGridView(); ConfigureBaseGrid(_invGrid);
            _invGrid.Columns.Add("Tab", "位置"); _invGrid.Columns.Add("Name", "名称");
            _invGrid.Columns.Add("Id", "内部 ID"); _invGrid.Columns.Add("Quality", "品质");
            _invGrid.Columns.Add("Count", "数量"); _invGrid.Columns.Add("Dur", "耐久");
            _invGrid.Columns.Add("Damage", "定义伤害");
            _invGrid.Columns[0].FillWeight = 73; _invGrid.Columns[1].FillWeight = 160;
            _invGrid.Columns[2].FillWeight = 155; _invGrid.Columns[3].FillWeight = 48;
            _invGrid.Columns[4].FillWeight = 48; _invGrid.Columns[5].FillWeight = 55;
            _invGrid.Columns[6].FillWeight = 64;
            _invGrid.SelectionChanged += delegate { UpdateInventoryEditorV012(); };
            root.Controls.Add(_invGrid, 0, 2);

            RoundedPanel card = BaseCard(0, 0); card.Dock = DockStyle.Fill;
            card.Margin = new Padding(0, 8, 0, 0); card.Padding = new Padding(14, 12, 14, 12);
            TableLayoutPanel edit = new TableLayoutPanel(); edit.Dock = DockStyle.Fill; edit.BackColor = Theme.Card;
            edit.Margin = Padding.Empty; edit.ColumnCount = 1; edit.RowCount = 4;
            edit.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            edit.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            edit.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
            edit.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); card.Controls.Add(edit);
            _invStatus = Theme.Label("进入游戏存档后，先点击‘初始化捕获’，再在游戏里打开背包，返回点击‘刷新库存’。", 8.4F, FontStyle.Regular, Theme.Muted);
            _invStatus.Dock = DockStyle.Fill; edit.Controls.Add(_invStatus, 0, 0);
            TableLayoutPanel line = new TableLayoutPanel(); line.Dock = DockStyle.Fill; line.Margin = Padding.Empty;
            line.ColumnCount = 6; line.RowCount = 1;
            for (int i = 0; i < 6; ++i) line.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F / 6F));
            _invAmount = InvNumericV012(1, 999999, 1); _invQuality = DarkCombo();
            foreach (string item in new string[] { "灰色 (0)", "绿色 (1)", "蓝色 (2)", "紫色 (3)", "橙色传奇 (4)", "铂金 (5)" }) _invQuality.Items.Add(item);
            _invQuality.SelectedIndex = 4; _invDurability = InvNumericV012(1, 100000, 100);
            _invRepairs = InvNumericV012(0, 999, 10); _invDamage = InvNumericV012(1, 50000, 3000);
            _invChangeDamage = new CheckBox(); _invChangeDamage.Text = "修改型号伤害"; _invChangeDamage.ForeColor = Theme.Text;
            _invChangeDamage.Dock = DockStyle.Fill; _invChangeDamage.Margin = new Padding(2, 11, 0, 0);
            _invChangeDamage.BackColor = Theme.Card;
            line.Controls.Add(InvLabeledV012("数量", _invAmount), 0, 0);
            line.Controls.Add(InvLabeledV012("品质", _invQuality), 1, 0);
            line.Controls.Add(InvLabeledV012("耐久", _invDurability), 2, 0);
            line.Controls.Add(InvLabeledV012("修理", _invRepairs), 3, 0);
            line.Controls.Add(InvLabeledV012("基础伤害", _invDamage), 4, 0);
            line.Controls.Add(_invChangeDamage, 5, 0);
            edit.Controls.Add(line, 0, 1);
            Label hint = Theme.Label("品质 = 物品实例 +0x68，橙色为 4；基础伤害 = 近战 Definition+0x240 / 枪械+0x354，影响全部同型号、仅当前游戏进程，退出时尝试恢复原值。请先备份存档。", 8F, FontStyle.Regular, Theme.Warning);
            hint.Dock = DockStyle.Fill; edit.Controls.Add(hint, 0, 2);
            TableLayoutPanel footer = new TableLayoutPanel(); footer.Dock = DockStyle.Fill;
            footer.Margin = Padding.Empty; footer.ColumnCount = 3; footer.RowCount = 1;
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 136));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 136));
            Label details = Theme.Label("材料仅修改已有背包数量；不凭空添加未知物品。单项写入前会核对 ID 与指针，读回可验证。", 8F, FontStyle.Regular, Theme.Muted);
            details.Dock = DockStyle.Fill; footer.Controls.Add(details, 0, 0);
            RoundedButton orange = ActionButton("选中设橙色", 128, false); orange.Dock = DockStyle.Fill;
            orange.Margin = new Padding(5, 4, 4, 4); orange.Click += delegate { ApplyInventoryEditorV012(true); };
            footer.Controls.Add(orange, 1, 0);
            RoundedButton apply = ActionButton("写入选中", 128, true); apply.Dock = DockStyle.Fill;
            apply.Margin = new Padding(4, 4, 0, 4); apply.Click += delegate { ApplyInventoryEditorV012(false); };
            footer.Controls.Add(apply, 2, 0);
            edit.Controls.Add(footer, 0, 3);
            root.Controls.Add(card, 0, 3);
            return page;
        }

        private NumericUpDown InvNumericV012(decimal min, decimal max, decimal value)
        {
            NumericUpDown n = new NumericUpDown(); n.Minimum = min; n.Maximum = max; n.Value = value;
            n.Dock = DockStyle.Fill; n.BackColor = Theme.Card2; n.ForeColor = Theme.Text;
            n.Font = Theme.Normal; n.Margin = new Padding(1, 3, 5, 3);
            n.BorderStyle = BorderStyle.FixedSingle;
            return n;
        }
        private Control InvLabeledV012(string name, Control inner)
        {
            TableLayoutPanel panel = new TableLayoutPanel(); panel.Dock = DockStyle.Fill; panel.Margin = Padding.Empty;
            panel.ColumnCount = 1; panel.RowCount = 2;
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 17));
            panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Label label = Theme.Label(name, 8F, FontStyle.Regular, Theme.Muted); label.Dock = DockStyle.Fill;
            panel.Controls.Add(label, 0, 0); panel.Controls.Add(inner, 0, 1);
            return panel;
        }
        private RuntimeInventoryItem SelectedInventoryV012()
        {
            if (_invGrid == null || _invGrid.SelectedRows.Count == 0) return null;
            return _invGrid.SelectedRows[0].Tag as RuntimeInventoryItem;
        }
        private void RefreshInventoryV012()
        {
            if (!RequireConnection()) return;
            RunBusy(delegate
            {
                try
                {
                    _game.EnsureInventoryCaptureV012();
                    _invSnapshot = _game.ReadInventoryV012();
                    ShowInventorySnapshotV012();
                    _invStatus.Text = "当前读取 " + _invSnapshot.Count + " 项（只有当前加载的背包）；不定时扫描。";
                    Log("背包读取：" + _invSnapshot.Count + " 项。");
                }
                catch (Exception ex) { _invStatus.Text = ex.Message; Log("读取背包：" + ex.Message); }
            });
        }
        private void ShowInventorySnapshotV012()
        {
            if (_invGrid == null) return;
            string term = (_invSearch == null ? "" : _invSearch.Text).Trim();
            RuntimeInventoryItem current = SelectedInventoryV012();
            long selected = current == null ? 0 : current.Address.ToInt64();
            _invGrid.SuspendLayout();
            try
            {
                _invGrid.Rows.Clear();
                int rowSelect = -1;
                foreach (RuntimeInventoryItem item in _invSnapshot)
                {
                    if (term.Length > 0 && (item.InternalId ?? "").IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0 &&
                        (item.LocalizedName ?? "").IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    string display = ItemChineseV012(item.InternalId);
                    if (display == item.InternalId && !String.IsNullOrWhiteSpace(item.LocalizedName)) display = item.LocalizedName;
                    int r = _invGrid.Rows.Add(item.Tab, display,
                        item.InternalId, item.Quality, item.Count, item.Weapon ? item.Durability.ToString("0.#") : "—",
                        item.Weapon ? item.BaseDamage.ToString("0.#") : "—");
                    _invGrid.Rows[r].Tag = item;
                    if (item.Address.ToInt64() == selected) rowSelect = r;
                }
                if (_invGrid.Rows.Count > 0) _invGrid.Rows[rowSelect < 0 ? 0 : rowSelect].Selected = true;
            }
            finally { _invGrid.ResumeLayout(); }
            UpdateInventoryEditorV012();
        }
        private void UpdateInventoryEditorV012()
        {
            RuntimeInventoryItem item = SelectedInventoryV012();
            if (item == null || _invAmount == null) return;
            _invAmount.Value = Math.Max(_invAmount.Minimum, Math.Min(_invAmount.Maximum, (decimal)Math.Max(1, item.Count)));
            _invQuality.SelectedIndex = Math.Max(0, Math.Min(5, item.Quality));
            _invDurability.Value = Math.Max(_invDurability.Minimum, Math.Min(_invDurability.Maximum,
                (decimal)(Double.IsNaN(item.Durability) || Double.IsInfinity(item.Durability) ? 1 : Math.Min(100000F, Math.Max(1F, item.Durability)))));
            _invRepairs.Value = Math.Max(0, Math.Min(_invRepairs.Maximum, (decimal)Math.Max(0, item.Repairs)));
            _invDamage.Value = Math.Max(1, Math.Min(_invDamage.Maximum,
                (decimal)(Double.IsNaN(item.BaseDamage) || Double.IsInfinity(item.BaseDamage) ? 1 : Math.Min(50000F, Math.Max(1F, item.BaseDamage)))));
            _invDurability.Enabled = item.Weapon;
            _invRepairs.Enabled = item.Weapon;
            _invDamage.Enabled = item.Weapon;
            _invChangeDamage.Enabled = item.Weapon;
            if (!item.Weapon) _invChangeDamage.Checked = false;
        }
        private void ApplyInventoryEditorV012(bool orangeOnly)
        {
            if (!RequireConnection()) return;
            RuntimeInventoryItem item = SelectedInventoryV012();
            if (item == null) return;
            if (orangeOnly && !item.Weapon) { MessageBox.Show(this, "只允许对武器设置橙色品质。", "背包", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            int count = orangeOnly ? item.Count : (int)_invAmount.Value;
            int quality = orangeOnly ? 4 : Math.Max(0, _invQuality.SelectedIndex);
            float dur = orangeOnly ? item.Durability : (float)_invDurability.Value;
            int repair = orangeOnly ? item.Repairs : (int)_invRepairs.Value;
            float damage = orangeOnly ? item.BaseDamage : (float)_invDamage.Value;
            bool changeDamage = !orangeOnly && _invChangeDamage.Checked;
            if (changeDamage && MessageBox.Show(this,
                "这次调整的是基础物品定义，所有同型号武器都会使用新伤害值。关闭工具时尝试恢复进程中的原值。确定继续？",
                "修改同型号伤害", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            RunBusy(delegate
            {
                try
                {
                    string status = _game.EditInventoryV012(item, count, quality, dur, repair, damage, changeDamage);
                    Log(status);
                    _invSnapshot = _game.ReadInventoryV012();
                    ShowInventorySnapshotV012();
                }
                catch (Exception ex) { Log("背包修改失败：" + ex.Message); MessageBox.Show(this, ex.Message, "写入失败", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            });
        }
    }
}
