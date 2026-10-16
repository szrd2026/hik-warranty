using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace HikWarranty
{
    /// <summary>
    /// 命令行版：hikq.exe
    ///   hikq LA0000000
    ///   hikq -f 序列号.txt -o 结果.csv
    ///   hikq -f sn.txt -j -c 8
    /// </summary>
    static class CliProgram
    {
        static int Main(string[] args)
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }

            var serials = new List<string>();
            string file = null, outCsv = null;
            int concurrency = 8;
            bool jsonOut = false;

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                switch (a)
                {
                    case "-h":
                    case "--help":
                    case "/?":
                        PrintUsage();
                        return 0;
                    case "-f":
                    case "--file":
                        if (++i >= args.Length) return UsageError("参数 " + a + " 缺少文件路径");
                        file = args[i];
                        break;
                    case "-o":
                    case "--out":
                        if (++i >= args.Length) return UsageError("参数 " + a + " 缺少输出路径");
                        outCsv = args[i];
                        break;
                    case "-c":
                    case "--concur":
                        if (++i >= args.Length || !int.TryParse(args[i], out concurrency) || concurrency < 1)
                            return UsageError("参数 " + a + " 需要 ≥1 的整数");
                        break;
                    case "-j":
                    case "--json":
                        jsonOut = true;
                        break;
                    default:
                        if (a.StartsWith("-")) return UsageError("未知参数：" + a);
                        serials.Add(a);
                        break;
                }
            }

            try
            {
                if (file != null)
                {
                    if (!File.Exists(file)) return UsageError("文件不存在：" + file);
                    serials = SnParser.Parse(ReadAllTextSmart(file));
                }
                if (serials.Count > 0)
                {
                    // 统一转大写、去重（位置参数与文件内容合并处理）
                    serials = SnParser.Parse(string.Join(Environment.NewLine, serials.ToArray()));
                }
            }
            catch (Exception ex) { return UsageError("读取文件失败：" + ex.Message); }

            if (serials.Count == 0)
            {
                PrintUsage();
                return 2;
            }

            var client = new WarrantyClient(15);
            int total = serials.Count, done = 0;

            // 预热：先建立一次 TLS 连接，避免第一条查询承担冷启动握手开销
            try { client.WarmupAsync().Wait(5000); } catch { }

            var stopwatch = Stopwatch.StartNew();
            Console.WriteLine("开始查询 " + total + " 个序列号（并发 " + concurrency + "）…");

            List<WarrantyInfo> results;
            using (var cts = new CancellationTokenSource())
            {
                ConsoleCancelEventHandler handler = delegate(object s, ConsoleCancelEventArgs e)
                {
                    e.Cancel = true; // Ctrl+C 先取消任务，再正常收尾
                    cts.Cancel();
                };
                Console.CancelKeyPress += handler;
                results = BatchRunner.Run(client, serials, concurrency, delegate(WarrantyInfo info)
                {
                    int n = Interlocked.Increment(ref done);
                    Console.WriteLine(string.Format("[{0}/{1}] {2}  {3}", n, total, Pad(info.Serial, 16), info.StatusText));
                }, cts.Token);
                Console.CancelKeyPress -= handler;
            }

            Console.WriteLine();

            if (jsonOut)
            {
                var json = new JavaScriptSerializer();
                json.MaxJsonLength = int.MaxValue;
                var list = new List<Dictionary<string, object>>();
                foreach (WarrantyInfo i in results)
                {
                    var d = new Dictionary<string, object>();
                    d["serial"] = i.Serial;
                    d["ok"] = i.Ok;
                    d["code"] = i.Code;
                    d["message"] = i.Ok ? i.Message : (i.Error.Length > 0 ? i.Error : i.Message);
                    d["model"] = i.Model;
                    d["warrantyEnd"] = i.EndDateDisplay;
                    d["daysToOutOfInsurance"] = i.HasDays ? (object)i.Days : null;
                    d["status"] = i.StatusText;
                    d["serviceLevel"] = i.ServiceLevelName;
                    d["serviceLevelMsg"] = i.ServiceLevelMsg;
                    d["configDesc"] = i.ConfigDesc;
                    d["serviceContent"] = i.ServiceContent;
                    d["imageUrl"] = i.ImageUrl;
                    d["elapsedMs"] = i.ElapsedMs;
                    list.Add(d);
                }
                Console.WriteLine(json.Serialize(list));
            }
            else
            {
                PrintTable(results);
                Console.WriteLine();
                Console.WriteLine("完成：共 " + results.Count + " 条 · 成功 " + CountOk(results) +
                                  " · 失败 " + (results.Count - CountOk(results)) +
                                  " · 用时 " + stopwatch.Elapsed.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture) + " s");
            }

            if (outCsv != null)
            {
                try
                {
                    WriteCsv(outCsv, results);
                    Console.WriteLine("CSV 已写入：" + Path.GetFullPath(outCsv));
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("写 CSV 失败：" + ex.Message);
                    return 1;
                }
            }

            return CountOk(results) == results.Count ? 0 : 1;
        }

        static int CountOk(List<WarrantyInfo> results)
        {
            int n = 0;
            foreach (WarrantyInfo r in results) if (r.Ok) n++;
            return n;
        }

        static void PrintTable(List<WarrantyInfo> results)
        {
            Console.WriteLine(Pad("序列号", 16) + Pad("型号", 20) + Pad("保修截止", 14) +
                              PadLeft("剩余天数", 10) + "  " + Pad("状态", 8) + Pad("服务级别", 18) + "说明");
            Console.WriteLine(new string('-', 100));
            foreach (WarrantyInfo i in results)
            {
                Console.WriteLine(Pad(i.Serial, 16) + Pad(Truncate(i.Model, 18), 20) + Pad(i.EndDateDisplay, 14) +
                                  PadLeft(i.DaysDisplay, 10) + "  " + Pad(i.StatusText, 8) +
                                  Pad(Truncate(i.ServiceLevelName, 16), 18) + Truncate(i.Note, 40));
            }
        }

        static void WriteCsv(string path, List<WarrantyInfo> results)
        {
            var sb = new StringBuilder();
            sb.AppendLine("\"#\",\"序列号\",\"型号\",\"保修截止\",\"剩余天数\",\"状态\",\"服务级别\",\"说明\",\"产品图片\"");
            for (int i = 0; i < results.Count; i++)
            {
                WarrantyInfo r = results[i];
                sb.Append(string.Join(",", new string[]
                {
                    Quote((i + 1).ToString(CultureInfo.InvariantCulture)),
                    Quote(r.Serial),
                    Quote(r.Model),
                    Quote(r.EndDateDisplay),
                    Quote(r.DaysDisplay),
                    Quote(r.StatusText),
                    Quote(r.ServiceLevelName),
                    Quote(r.Note),
                    Quote(r.ImageUrl)
                }));
                sb.AppendLine();
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true)); // 带 BOM，Excel 直接打开不乱码
        }

        static string Quote(string s)
        {
            return "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
        }

        static string Truncate(string s, int max)
        {
            if (s == null) return "";
            return s.Length <= max ? s : s.Substring(0, max - 1) + "…";
        }

        // ------- 等宽对齐（中文按 2 列计）

        static int DisplayWidth(string s)
        {
            int w = 0;
            foreach (char c in s) w += IsWide(c) ? 2 : 1;
            return w;
        }

        static bool IsWide(char c)
        {
            return (c >= 0x1100 && c <= 0x115F) || (c >= 0x2E80 && c <= 0xA4CF) ||
                   (c >= 0xAC00 && c <= 0xD7A3) || (c >= 0xF900 && c <= 0xFAFF) ||
                   (c >= 0xFE30 && c <= 0xFE4F) || (c >= 0xFF00 && c <= 0xFF60) ||
                   (c >= 0xFFE0 && c <= 0xFFE6);
        }

        static string Pad(string s, int width)
        {
            if (s == null) s = "";
            var sb = new StringBuilder(s);
            int w = DisplayWidth(s);
            while (w < width) { sb.Append(' '); w++; }
            return sb.ToString();
        }

        static string PadLeft(string s, int width)
        {
            if (s == null) s = "";
            var sb = new StringBuilder();
            int w = DisplayWidth(s);
            while (w < width) { sb.Append(' '); w++; }
            sb.Append(s);
            return sb.ToString();
        }

        static string ReadAllTextSmart(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            try { return new UTF8Encoding(false, true).GetString(bytes); }
            catch { return Encoding.GetEncoding(936).GetString(bytes); }
        }

        static int UsageError(string message)
        {
            Console.Error.WriteLine("错误：" + message);
            Console.Error.WriteLine();
            PrintUsage();
            return 2;
        }

        static void PrintUsage()
        {
            Console.WriteLine(@"海康威视维保查询（命令行版）hikq

用法：
  hikq <序列号> [更多序列号 ...]
  hikq -f <序列号文件> [-o <结果.csv>] [-c 并发数] [-j]

参数：
  -f, --file    从文本文件读取序列号（每行一个，也支持空格/逗号分隔）
  -o, --out     将结果导出为 CSV（UTF-8 带 BOM，Excel 直接打开）
  -c, --concur  并发数（默认 8）
  -j, --json    以 JSON 输出结果
  -h, --help    显示本帮助

示例：
  hikq LA0000000
  hikq -f sn.txt -o 结果.csv
  hikq LA0000000 SN0000001 -c 4 -j

退出码：0 = 全部查询成功；1 = 存在未查到/失败；2 = 参数错误");
        }
    }
}