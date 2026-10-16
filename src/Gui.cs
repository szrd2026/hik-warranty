using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace HikWarranty
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e)
            {
                MessageBox.Show("程序出现未处理的异常：\r\n\r\n" + e.Exception.Message,
                    "海康维保查询", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };

            Application.Run(new MainForm(args));
        }
    }

    public sealed class MainForm : Form
    {
        // 列索引
        const int CIdx = 0, CSn = 1, CModel = 2, CEnd = 3, CDays = 4, CStatus = 5, CSvc = 6, CNote = 7;

        static readonly string FontName = FindFont("Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI");

        // 屏幕 DPI 缩放系数（96 DPI = 1.0）。所有布局尺寸乘以它，字号用磅值由系统按 DPI 渲染，
        // 两者结合保证 100% / 125% / 150% / 200% 缩放下界面比例一致。
        readonly float _k;

        readonly TextBox _input;
        readonly Button _btnQuery, _btnCancel, _btnClear, _btnExport, _btnCopy;
        readonly ComboBox _cboConc;
        readonly Label _lblConc;
        readonly DataGridView _grid;
        readonly ToolStripStatusLabel _lblStatus, _lblTime;
        readonly ToolStripProgressBar _progress;
        readonly ContextMenuStrip _menu;
        readonly ToolStripMenuItem _menuDetail, _menuCopySn, _menuCopyRow, _menuImage;

        readonly WarrantyClient _client = new WarrantyClient(15);
        bool _autoRun;
        bool _running;
        CancellationTokenSource _cts;

        public MainForm(string[] args)
        {
            using (Graphics screen = Graphics.FromHwnd(IntPtr.Zero))
                _k = screen.DpiX / 96f;

            // ------- 窗口
            Text = "海康威视维保查询";
            Font = new Font(FontName, 9F);
            AutoScaleMode = AutoScaleMode.None;   // 布局缩放由 _k 手动控制，避免与字体自动缩放叠加
            ClientSize = new Size(S(1000), S(640));
            MinimumSize = new Size(S(780), S(480));
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;

            // ------- 输入区
            var lblInput = new Label
            {
                Text = "序列号（每行一个，也可用空格 / 逗号分隔；大小写不限，程序自动转大写）",
                AutoSize = true,
                Location = new Point(S(12), S(10))
            };

            _input = new TextBox
            {
                Location = new Point(S(12), S(32)),
                Size = new Size(S(976), S(80)),
                Multiline = true,
                WordWrap = false,
                ScrollBars = ScrollBars.Vertical,
                AcceptsReturn = true,
                AllowDrop = true,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Font = new Font("Consolas", 10F)
            };

            // ------- 按钮行
            _btnQuery  = MakeButton("查询 (F5)", S(12), S(120), S(110), AnchorStyles.Top | AnchorStyles.Left);
            _btnCancel = MakeButton("停止",       S(128), S(120), S(72), AnchorStyles.Top | AnchorStyles.Left);
            _btnClear  = MakeButton("清空",       S(206), S(120), S(72), AnchorStyles.Top | AnchorStyles.Left);
            _btnExport = MakeButton("导出 CSV",   S(284), S(120), S(96), AnchorStyles.Top | AnchorStyles.Left);
            _btnCopy   = MakeButton("复制结果",   S(386), S(120), S(96), AnchorStyles.Top | AnchorStyles.Left);
            _btnQuery.Font = new Font(FontName, 9F, FontStyle.Bold);
            _btnCancel.Enabled = false;

            _lblConc = new Label { Text = "并发：", AutoSize = true, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            _cboConc = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = S(64),
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            _cboConc.Items.AddRange(new object[] { "1", "2", "4", "6", "8", "12", "16" });
            _cboConc.SelectedIndex = 4; // 8（实测 8 并发最快，16 会因服务端限速变慢）

            // ------- 结果表
            _grid = new DataGridView
            {
                Location = new Point(S(12), S(160)),
                Size = new Size(S(976), ClientSize.Height - S(160) - S(24)),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                AllowUserToOrderColumns = true,
                ReadOnly = true,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = true,
                BackgroundColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
                ColumnHeadersHeight = S(30),
                RowTemplate = { Height = S(24) },
                EnableHeadersVisualStyles = false
            };
            _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(240, 242, 245);
            _grid.ColumnHeadersDefaultCellStyle.Font = new Font(FontName, 9F, FontStyle.Bold);
            _grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(249, 250, 251);
            _grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(214, 232, 255);
            _grid.DefaultCellStyle.SelectionForeColor = Color.Black;
            SetDoubleBuffered(_grid);

            _grid.Columns.Add(MakeColumn("idx", "#", S(42), DataGridViewContentAlignment.MiddleCenter, false, false));
            _grid.Columns.Add(MakeColumn("sn", "序列号", S(130), DataGridViewContentAlignment.MiddleLeft, true, false));
            _grid.Columns.Add(MakeColumn("model", "型号", S(150), DataGridViewContentAlignment.MiddleLeft, true, false));
            _grid.Columns.Add(MakeColumn("end", "保修截止", S(100), DataGridViewContentAlignment.MiddleCenter, true, false));
            _grid.Columns.Add(MakeColumn("days", "剩余天数", S(84), DataGridViewContentAlignment.MiddleCenter, true, false));
            _grid.Columns.Add(MakeColumn("status", "状态", S(76), DataGridViewContentAlignment.MiddleCenter, true, false));
            _grid.Columns.Add(MakeColumn("svc", "服务级别", S(130), DataGridViewContentAlignment.MiddleLeft, true, false));
            _grid.Columns.Add(MakeColumn("note", "说明", S(160), DataGridViewContentAlignment.MiddleLeft, true, true));

            // ------- 右键菜单
            _menuDetail = new ToolStripMenuItem("查看详情(&D)", null, delegate { ShowDetail(CurrentInfo()); });
            _menuCopySn = new ToolStripMenuItem("复制序列号(&C)", null, delegate { CopyCurrentCell(CSn); });
            _menuCopyRow = new ToolStripMenuItem("复制整行(&R)", null, delegate { CopySelectedRows(); });
            _menuImage = new ToolStripMenuItem("打开产品图片(&P)", null, delegate { OpenImage(CurrentInfo()); });
            _menu = new ContextMenuStrip();
            _menu.Items.Add(_menuDetail);
            _menu.Items.Add(_menuImage);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(_menuCopySn);
            _menu.Items.Add(_menuCopyRow);
            _menu.Opening += delegate
            {
                WarrantyInfo info = CurrentInfo();
                bool has = info != null;
                _menuDetail.Enabled = has;
                _menuCopySn.Enabled = has;
                _menuCopyRow.Enabled = _grid.SelectedRows.Count > 0;
                _menuImage.Enabled = has && info.ImageUrl.Length > 0;
            };
            _grid.ContextMenuStrip = _menu;

            // ------- 状态栏
            var status = new StatusStrip();
            _lblStatus = new ToolStripStatusLabel("就绪：输入序列号后按 Enter 或 F5 查询");
            _lblStatus.Spring = true;
            _lblStatus.TextAlign = ContentAlignment.MiddleLeft;
            _progress = new ToolStripProgressBar { Width = S(180), Visible = false };
            _lblTime = new ToolStripStatusLabel("耗时 --");
            _lblTime.AutoSize = true;
            status.Items.Add(_lblStatus);
            status.Items.Add(_progress);
            status.Items.Add(_lblTime);

            Controls.Add(_input);
            Controls.Add(lblInput);
            Controls.Add(_btnQuery);
            Controls.Add(_btnCancel);
            Controls.Add(_btnClear);
            Controls.Add(_btnExport);
            Controls.Add(_btnCopy);
            Controls.Add(_lblConc);
            Controls.Add(_cboConc);
            Controls.Add(_grid);
            Controls.Add(status);

            // 右侧“并发”位置按窗口宽度对齐
            _cboConc.Location = new Point(ClientSize.Width - S(12) - _cboConc.Width, S(123));
            _lblConc.Location = new Point(_cboConc.Left - _lblConc.Width - S(6), S(127));

            // ------- 事件
            _btnQuery.Click += delegate { StartQuery(); };
            _btnCancel.Click += delegate { CancelQuery(); };
            _btnClear.Click += delegate { _input.Clear(); _input.Focus(); };
            _btnExport.Click += delegate { ExportCsv(); };
            _btnCopy.Click += delegate { CopyResultTable(); };
            _grid.CellFormatting += GridCellFormatting;
            _grid.CellDoubleClick += delegate(object s, DataGridViewCellEventArgs e) { if (e.RowIndex >= 0) ShowDetail(CurrentInfo()); };
            _input.KeyDown += InputKeyDown;
            _input.DragEnter += InputDragEnter;
            _input.DragDrop += InputDragDrop;
            _grid.KeyDown += GridKeyDown;

            // ------- 命令行参数：序列号或 .txt 文件
            foreach (string a in args)
            {
                try
                {
                    if (File.Exists(a))
                    {
                        _input.Text = ReadAllTextSmart(a);
                    }
                    else
                    {
                        if (_input.TextLength > 0) _input.Text += Environment.NewLine;
                        _input.Text += a;
                    }
                }
                catch { }
            }
            _autoRun = _input.TextLength > 0;

            LoadSettings();
            Task.Run(() => _client.WarmupAsync()); // 预热连接，不阻塞界面
        }

        int S(int value)
        {
            return (int)Math.Round(value * _k);
        }

        // ================================================================ 查询

        async void StartQuery()
        {
            if (_running) return;
            List<string> sns = SnParser.Parse(_input.Text);
            if (sns.Count == 0)
            {
                MessageBox.Show(this, "请先输入序列号。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                _input.Focus();
                return;
            }

            _running = true;
            _cts = new CancellationTokenSource();
            CancellationToken ct = _cts.Token;
            int concurrency = int.Parse((string)_cboConc.SelectedItem, CultureInfo.InvariantCulture);

            _grid.Rows.Clear();
            var rowIndex = new Dictionary<string, int>();
            for (int i = 0; i < sns.Count; i++)
            {
                int idx = _grid.Rows.Add(i + 1, sns[i], "", "", "", "查询中…", "", "");
                rowIndex[sns[i]] = idx;
            }

            SetRunning(true, sns.Count);
            WarnIfLarge(sns.Count);

            var stopwatch = Stopwatch.StartNew();
            int done = 0;
            List<WarrantyInfo> results = await Task.Run(() => BatchRunner.Run(
                _client, sns, concurrency,
                delegate(WarrantyInfo info)
                {
                    int n = Interlocked.Increment(ref done);
                    try
                    {
                        BeginInvoke(new Action(delegate { ApplyResult(rowIndex, info, n, sns.Count, stopwatch.Elapsed); }));
                    }
                    catch { }
                }, ct));

            SetRunning(false, sns.Count);
            _progress.Value = 0;
            _progress.Visible = false;

            int ok = 0, expired = 0, fail = 0;
            foreach (WarrantyInfo r in results)
            {
                if (!r.Ok) fail++;
                else if (r.Expired) expired++;
                else ok++;
            }
            _lblStatus.Text = string.Format("完成：共 {0} 条 · 在保 {1} · 已过保 {2} · 未查到/失败 {3}", results.Count, ok, expired, fail);
            _lblTime.Text = string.Format("耗时 {0:0.00} s", stopwatch.Elapsed.TotalSeconds);

            _cts.Dispose();
            _cts = null;
        }

        void CancelQuery()
        {
            if (_cts != null)
            {
                _btnCancel.Enabled = false;
                _lblStatus.Text = "正在停止…";
                try { _cts.Cancel(); } catch { }
            }
        }

        void WarnIfLarge(int count)
        {
            if (count > 100)
                _lblStatus.Text = string.Format("正在查询 {0} 条（并发 {1}），请保持网络畅通…", count, _cboConc.SelectedItem);
        }

        void ApplyResult(Dictionary<string, int> rowIndex, WarrantyInfo info, int done, int total, TimeSpan elapsed)
        {
            if (IsDisposed) return;
            int idx;
            if (rowIndex.TryGetValue(info.Serial, out idx) && idx < _grid.Rows.Count)
            {
                DataGridViewRow row = _grid.Rows[idx];
                row.Tag = info;
                row.Cells[CModel].Value = info.Model;
                row.Cells[CEnd].Value = info.EndDateDisplay;
                row.Cells[CDays].Value = info.HasDays ? (object)info.Days : (object)int.MinValue;
                row.Cells[CStatus].Value = info.StatusText;
                row.Cells[CSvc].Value = info.ServiceLevelName;
                row.Cells[CNote].Value = info.Note;
                row.Cells[CDays].ToolTipText = info.HasDays
                    ? (info.Days >= 0 ? "距离过保还有 " + info.Days + " 天" : "已过保 " + (-info.Days) + " 天")
                    : "";
                row.Cells[CNote].ToolTipText = info.Note;
            }
            _progress.Maximum = Math.Max(1, total);
            _progress.Value = Math.Min(done, _progress.Maximum);
            _lblTime.Text = string.Format("耗时 {0:0.0} s", elapsed.TotalSeconds);
            if (_running && done < total) _lblStatus.Text = string.Format("查询中… {0}/{1}", done, total);
        }

        void SetRunning(bool running, int total)
        {
            _running = running;
            _btnQuery.Enabled = !running;
            _btnClear.Enabled = !running;
            _btnCancel.Enabled = running;
            _cboConc.Enabled = !running;
            _progress.Visible = running;
            if (running)
            {
                _progress.Maximum = Math.Max(1, total);
                _progress.Value = 0;
                _lblStatus.Text = string.Format("查询中… 0/{0}", total);
                _lblTime.Text = "耗时 0.0 s";
            }
        }

        // ================================================================ 表格绘制

        void GridCellFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            DataGridViewRow row = _grid.Rows[e.RowIndex];
            WarrantyInfo info = row.Tag as WarrantyInfo;

            string name = _grid.Columns[e.ColumnIndex].Name;
            if (name == "days")
            {
                if (e.Value is int && (int)e.Value == int.MinValue)
                {
                    e.Value = "";
                    e.FormattingApplied = true;
                }
            }

            if (info == null) return;
            if (name == "days" || name == "status")
            {
                Color c;
                if (!info.Ok) c = Color.FromArgb(130, 130, 130);
                else if (info.Expired) c = Color.FromArgb(200, 60, 60);
                else c = Color.FromArgb(0, 130, 64);
                e.CellStyle.ForeColor = c;
                e.CellStyle.SelectionForeColor = c;
            }
        }

        // ================================================================ 导出 / 复制

        static readonly string[] CsvHeaders = { "#", "序列号", "型号", "保修截止", "剩余天数", "状态", "服务级别", "说明", "产品图片" };

        string[] RowToArray(WarrantyInfo info, int index)
        {
            return new string[]
            {
                index.ToString(CultureInfo.InvariantCulture),
                info.Serial,
                info.Model,
                info.EndDateDisplay,
                info.DaysDisplay,
                info.StatusText,
                info.ServiceLevelName,
                info.Note,
                info.ImageUrl
            };
        }

        void ExportCsv()
        {
            var infos = new List<WarrantyInfo>();
            foreach (DataGridViewRow row in _grid.Rows)
            {
                WarrantyInfo info = row.Tag as WarrantyInfo;
                if (info != null) infos.Add(info);
            }
            if (infos.Count == 0)
            {
                MessageBox.Show(this, "当前没有可导出的查询结果。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (var dlg = new SaveFileDialog())
            {
                dlg.Filter = "CSV 文件 (*.csv)|*.csv|文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*";
                dlg.FileName = "维保查询_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".csv";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                var sb = new StringBuilder();
                sb.AppendLine(string.Join(",", QuoteAll(CsvHeaders)));
                for (int i = 0; i < infos.Count; i++)
                    sb.AppendLine(string.Join(",", QuoteAll(RowToArray(infos[i], i + 1))));

                try
                {
                    File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true)); // 带 BOM，Excel 打开不乱码
                    _lblStatus.Text = "已导出：" + dlg.FileName;
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "导出失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        static string[] QuoteAll(string[] values)
        {
            var copy = new string[values.Length];
            for (int i = 0; i < values.Length; i++)
                copy[i] = "\"" + (values[i] ?? "").Replace("\"", "\"\"") + "\"";
            return copy;
        }

        void CopyResultTable()
        {
            var infos = new List<WarrantyInfo>();
            foreach (DataGridViewRow row in _grid.Rows)
            {
                WarrantyInfo info = row.Tag as WarrantyInfo;
                if (info != null) infos.Add(info);
            }
            if (infos.Count == 0)
            {
                MessageBox.Show(this, "当前没有可复制的结果。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine(string.Join("\t", CsvHeaders));
            for (int i = 0; i < infos.Count; i++)
                sb.AppendLine(string.Join("\t", RowToArray(infos[i], i + 1)));

            try
            {
                Clipboard.SetText(sb.ToString());
                _lblStatus.Text = string.Format("已复制 {0} 条结果到剪贴板（可直接粘贴到 Excel）", infos.Count);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "复制失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // ================================================================ 详情

        static string BuildDetailText(WarrantyInfo i)
        {
            var sb = new StringBuilder();
            sb.AppendLine("序列号:   " + i.Serial);
            string days = i.HasDays
                ? (i.Days >= 0 ? "（距离过保还有 " + i.Days + " 天）" : "（已过保 " + (-i.Days) + " 天）")
                : "";
            sb.AppendLine("状态:     " + i.StatusText + days);
            if (i.Model.Length > 0)          sb.AppendLine("型号:     " + i.Model);
            if (i.EndDateDisplay.Length > 0) sb.AppendLine("保修截止: " + i.EndDateDisplay);
            if (i.ServiceLevelName.Length > 0)
                sb.AppendLine("服务级别: " + i.ServiceLevelName + (i.ServiceLevel.Length > 0 ? "（等级 " + i.ServiceLevel + "）" : ""));
            if (i.ServiceLevelMsg.Length > 0) sb.AppendLine("服务说明: " + i.ServiceLevelMsg);
            if (i.ConfigDesc.Length > 0)      sb.AppendLine("配置说明: " + i.ConfigDesc);
            if (i.ServiceContent.Length > 0)  sb.AppendLine("服务内容: " + i.ServiceContent);
            if (i.ServiceEquity.Length > 0)   sb.AppendLine("服务权益: " + i.ServiceEquity);
            if (i.MaterialGroupName.Length > 0) sb.AppendLine("物料组:   " + i.MaterialGroupName);
            if (i.ProductType.Length > 0)     sb.AppendLine("产品类型: " + i.ProductType);
            if (i.OemQueryState.Length > 0)   sb.AppendLine("OEM 查询: " + i.OemQueryState);
            if (i.OldExchangeNewFlag)         sb.AppendLine("以旧换新: 是");
            if (i.StopServiceFlag)            sb.AppendLine("停止服务: 是");
            if (i.HardDiskMaterialFlag)       sb.AppendLine("硬盘物料: 是");
            if (i.NewBizFlag)                 sb.AppendLine("新业务:   是");
            if (i.BizMessage.Length > 0)      sb.AppendLine("业务提示: " + i.BizMessage);
            if (i.Message.Length > 0)         sb.AppendLine("接口消息: " + i.Message);
            if (i.Error.Length > 0)           sb.AppendLine("错误:     " + i.Error);
            sb.AppendLine("接口代码: " + i.Code + "    单条耗时: " + i.ElapsedMs + " ms");
            if (i.ImageUrl.Length > 0)        sb.AppendLine("产品图片: " + i.ImageUrl);
            if (i.RawJson.Length > 0)
            {
                sb.AppendLine();
                sb.AppendLine("────────── 原始响应 ──────────");
                sb.AppendLine(i.RawJson);
            }
            return sb.ToString();
        }

        void ShowDetail(WarrantyInfo info)
        {
            if (info == null) return;

            var form = new Form();
            form.Text = "维保详情 - " + info.Serial;
            form.Font = new Font(FontName, 9F);
            form.AutoScaleMode = AutoScaleMode.None;
            form.ClientSize = new Size(S(640), S(460));
            form.MinimumSize = new Size(S(420), S(300));
            form.StartPosition = FormStartPosition.CenterParent;
            form.ShowIcon = false;
            form.MinimizeBox = false;
            form.MaximizeBox = false;

            var box = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Dock = DockStyle.Fill,
                BackColor = Color.White,
                Text = BuildDetailText(info)
            };

            var panel = new Panel { Dock = DockStyle.Bottom, Height = S(44) };
            var btnImg = new Button { Text = "打开产品图片", Location = new Point(S(8), S(8)), Size = new Size(S(120), S(28)), Enabled = info.ImageUrl.Length > 0 };
            var btnCopyAll = new Button { Text = "复制全部", Location = new Point(S(136), S(8)), Size = new Size(S(96), S(28)) };
            var btnClose = new Button { Text = "关闭", Size = new Size(S(88), S(28)), Anchor = AnchorStyles.Top | AnchorStyles.Right, DialogResult = DialogResult.Cancel };
            btnClose.Location = new Point(panel.Width - S(8) - btnClose.Width, S(8));
            btnImg.Click += delegate { OpenUrl(info.ImageUrl); };
            btnCopyAll.Click += delegate
            {
                try { Clipboard.SetText(box.Text); } catch { }
            };
            panel.Controls.Add(btnImg);
            panel.Controls.Add(btnCopyAll);
            panel.Controls.Add(btnClose);

            form.Controls.Add(box);     // Fill 先加，最后才分配剩余空间
            form.Controls.Add(panel);
            form.CancelButton = btnClose;
            form.ShowDialog(this);
        }

        static void OpenUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { }
        }

        // ================================================================ 输入辅助

        void InputKeyDown(object sender, KeyEventArgs e)
        {
            if ((int)e.KeyCode == 229) return; // 输入法组合中

            if (e.Control && e.KeyCode == Keys.A)
            {
                _input.SelectAll();
                e.SuppressKeyPress = true;
                return;
            }
            if (e.KeyCode != Keys.Enter) return;
            if (e.Control || e.Alt || e.Shift) return;
            if (_input.Lines.Length <= 1)              // 单行时回车直接查询
            {
                e.SuppressKeyPress = true;
                e.Handled = true;
                StartQuery();
            }
        }

        void InputDragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.UnicodeText))
                e.Effect = DragDropEffects.Copy;
            else
                e.Effect = DragDropEffects.None;
        }

        void InputDragDrop(object sender, DragEventArgs e)
        {
            try
            {
                var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files != null && files.Length > 0)
                {
                    _input.Text = ReadAllTextSmart(files[0]);
                    return;
                }
                string text = e.Data.GetData(DataFormats.UnicodeText) as string;
                if (text != null) _input.Text = text;
            }
            catch { }
        }

        static string ReadAllTextSmart(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            try { return new UTF8Encoding(false, true).GetString(bytes); }
            catch { return Encoding.GetEncoding(936).GetString(bytes); } // GBK 兜底
        }

        void GridKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.C)
            {
                CopySelectedRows();
                e.SuppressKeyPress = true;
            }
        }

        void CopySelectedRows()
        {
            if (_grid.SelectedRows.Count == 0) return;
            var sb = new StringBuilder();
            foreach (DataGridViewRow row in _grid.SelectedRows)
            {
                WarrantyInfo info = row.Tag as WarrantyInfo;
                if (info != null)
                    sb.AppendLine(string.Join("\t", RowToArray(info, row.Index + 1)));
            }
            try { Clipboard.SetText(sb.ToString()); } catch { }
        }

        void CopyCurrentCell(int column)
        {
            if (_grid.CurrentRow == null) return;
            object v = _grid.CurrentRow.Cells[column].Value;
            try { Clipboard.SetText(v == null ? "" : Convert.ToString(v, CultureInfo.InvariantCulture)); } catch { }
        }

        WarrantyInfo CurrentInfo()
        {
            if (_grid.CurrentRow == null) return null;
            return _grid.CurrentRow.Tag as WarrantyInfo;
        }

        void OpenImage(WarrantyInfo info)
        {
            if (info != null && info.ImageUrl.Length > 0) OpenUrl(info.ImageUrl);
        }

        // ================================================================ 初始化辅助

        Button MakeButton(string text, int x, int y, int width, AnchorStyles anchor)
        {
            return new Button
            {
                Text = text,
                Location = new Point(x, y),
                Size = new Size(width, S(30)),
                Anchor = anchor,
                FlatStyle = FlatStyle.System
            };
        }

        static DataGridViewTextBoxColumn MakeColumn(string name, string header, int width,
            DataGridViewContentAlignment align, bool sortable, bool fill)
        {
            var c = new DataGridViewTextBoxColumn();
            c.Name = name;
            c.HeaderText = header;
            c.Width = width;
            c.SortMode = sortable ? DataGridViewColumnSortMode.Automatic : DataGridViewColumnSortMode.NotSortable;
            c.DefaultCellStyle.Alignment = align;
            if (fill)
            {
                c.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
                c.MinimumWidth = width;
                c.FillWeight = 100;
            }
            return c;
        }

        static string FindFont(params string[] names)
        {
            try
            {
                var installed = FontFamily.Families;
                foreach (string want in names)
                {
                    foreach (FontFamily f in installed)
                    {
                        if (string.Equals(f.Name, want, StringComparison.OrdinalIgnoreCase))
                            return f.Name;
                    }
                }
            }
            catch { }
            return FontFamily.GenericSansSerif.Name;
        }

        static void SetDoubleBuffered(DataGridView grid)
        {
            try
            {
                PropertyInfo pi = typeof(DataGridView).GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic);
                if (pi != null) pi.SetValue(grid, true, null);
            }
            catch { }
        }

        // ================================================================ 窗口设置持久化

        static string SettingsPath()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HikWarranty");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "ui.ini");
        }

        void LoadSettings()
        {
            try
            {
                if (!File.Exists(SettingsPath())) return;
                foreach (string line in File.ReadAllLines(SettingsPath()))
                {
                    int p = line.IndexOf('=');
                    if (p <= 0) continue;
                    string key = line.Substring(0, p).Trim();
                    string val = line.Substring(p + 1).Trim();
                    if (key == "width") { int w; if (int.TryParse(val, out w) && w >= MinimumSize.Width) Width = w; }
                    else if (key == "height") { int h; if (int.TryParse(val, out h) && h >= MinimumSize.Height) Height = h; }
                    else if (key == "max") { if (val == "1") WindowState = FormWindowState.Maximized; }
                    else if (key == "conc")
                    {
                        int i = _cboConc.Items.IndexOf(val);
                        if (i >= 0) _cboConc.SelectedIndex = i;
                    }
                }
            }
            catch { }
        }

        void SaveSettings()
        {
            try
            {
                bool max = WindowState == FormWindowState.Maximized;
                Size restore = max ? RestoreBounds.Size : Size;
                File.WriteAllLines(SettingsPath(), new string[]
                {
                    "width=" + restore.Width,
                    "height=" + restore.Height,
                    "max=" + (max ? "1" : "0"),
                    "conc=" + Convert.ToString(_cboConc.SelectedItem, CultureInfo.InvariantCulture)
                });
            }
            catch { }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (_autoRun)
            {
                _autoRun = false;
                BeginInvoke(new Action(StartQuery));
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_cts != null)
            {
                try { _cts.Cancel(); } catch { }
            }
            SaveSettings();
            base.OnFormClosing(e);
        }
    }
}