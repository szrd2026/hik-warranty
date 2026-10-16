using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace HikWarranty
{
    /// <summary>
    /// 海康威视合作伙伴平台「维保查询」接口客户端。
    ///
    /// 接口：POST https://partners.hikvision.com/support/psp-api/repair/csp/product/getSnWarrantyInfo
    /// 请求：{"serialNumber":"LA0000000"}   （注意：序列号区分大小写，必须大写）
    /// 成功：{"code":"000","msg":"成功","data":{...},"success":true}
    /// 失败：{"code":"998","msg":"序列号不存在或已被删除","success":false}
    ///
    /// 无需登录、无需 Cookie；但请保持请求频率温和，避免给官方服务器造成压力。
    /// </summary>
    public sealed class WarrantyClient : IDisposable
    {
        const string BaseUrl  = "https://partners.hikvision.com/";
        const string QueryUrl = "support/psp-api/repair/csp/product/getSnWarrantyInfo";
        const string WarmUrl  = "support/maintenance/inquiry";

        static WarrantyClient()
        {
            // .NET Framework 默认每台主机只允许 2 个并发连接，会把批量查询卡住。
            ServicePointManager.DefaultConnectionLimit = 64;
            ServicePointManager.Expect100Continue = false;
            ServicePointManager.CheckCertificateRevocationList = false; // 少一次握手往返
            try { ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12; } catch { }
        }

        readonly HttpClient _http;
        readonly JavaScriptSerializer _json = new JavaScriptSerializer();

        public WarrantyClient(int timeoutSeconds)
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                UseCookies = true,
                CookieContainer = new CookieContainer(),
                AllowAutoRedirect = true
            };
            _http = new HttpClient(handler);
            _http.BaseAddress = new Uri(BaseUrl);
            if (timeoutSeconds > 0) _http.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
            _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
            _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
            _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");
            _http.DefaultRequestHeaders.Referrer = new Uri(BaseUrl + WarmUrl);
        }

        /// <summary>预热：先访问一次查询首页，建立 TLS 连接与 Cookie，之后的首个查询更快。</summary>
        public async Task WarmupAsync()
        {
            try { using (var resp = await _http.GetAsync(WarmUrl).ConfigureAwait(false)) { } }
            catch { /* 预热失败不影响查询 */ }
        }

        /// <summary>查询单个序列号。任何异常都会被捕获并写入 <see cref="WarrantyInfo.Error"/>。</summary>
        public async Task<WarrantyInfo> QueryAsync(string serialNumber, CancellationToken ct)
        {
            string sn = (serialNumber ?? "").Trim().ToUpperInvariant();
            var info = new WarrantyInfo { Serial = sn };
            if (sn.Length == 0) { info.Error = "序列号为空"; return info; }

            byte[] body = Encoding.UTF8.GetBytes("{\"serialNumber\":\"" + JsonEscape(sn) + "\"}");
            var sw = Stopwatch.StartNew();

            for (int attempt = 1; ; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using (var req = new HttpRequestMessage(HttpMethod.Post, QueryUrl))
                    {
                        req.Content = new ByteArrayContent(body);
                        req.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json");

                        using (var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false))
                        {
                            int status = (int)resp.StatusCode;
                            byte[] bytes = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);

                            if (status == 200)
                            {
                                info.ElapsedMs = sw.ElapsedMilliseconds;
                                string text = DecodeUtf8(bytes);
                                info.RawJson = text;
                                Parse(text, info);
                                return info;
                            }

                            if (attempt < 3 && (status >= 500 || status == 429))
                            {
                                await Task.Delay(250 * attempt, ct).ConfigureAwait(false);
                                continue;
                            }

                            info.Error = "HTTP " + status;
                            info.ElapsedMs = sw.ElapsedMilliseconds;
                            return info;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    if (ct.IsCancellationRequested) throw;
                    if (attempt < 3) { Thread.Sleep(250 * attempt); continue; }
                    info.Error = "请求超时";
                    info.ElapsedMs = sw.ElapsedMilliseconds;
                    return info;
                }
                catch (HttpRequestException ex)
                {
                    if (attempt < 3) { Thread.Sleep(250 * attempt); continue; }
                    info.Error = "网络错误：" + ex.Message;
                    info.ElapsedMs = sw.ElapsedMilliseconds;
                    return info;
                }
            }
        }

        // ---------------------------------------------------------------- 解析

        void Parse(string text, WarrantyInfo info)
        {
            Dictionary<string, object> root;
            try { root = _json.DeserializeObject(text) as Dictionary<string, object>; }
            catch (Exception ex) { info.Error = "响应解析失败：" + ex.Message; return; }
            if (root == null) { info.Error = "响应格式异常"; return; }

            info.Code = Str(root, "code");
            info.Message = Str(root, "msg");

            Dictionary<string, object> data = Obj(root, "data");
            if (!Bool(root, "success") || data == null)
            {
                if (info.Message.Length == 0) info.Message = "查询失败（code=" + info.Code + "）";
                return;
            }

            info.Ok = true;
            info.Model            = Str(data, "labelModel");
            info.WarrantyEndRaw   = Str(data, "warrantyExpirationTime");
            info.ServiceLevel     = Str(data, "serviceLevel");
            info.ServiceLevelName = Str(data, "serviceLevelName");
            info.ServiceLevelMsg  = Str(data, "serviceLevelMsg");
            info.ImageUrl         = Str(data, "productionImgUrl");
            info.ProductType      = Str(data, "productType");
            info.OemQueryState    = Str(data, "oemQueryState");
            info.ConfigDesc       = Str(data, "configDesc");
            info.ServiceContent   = Str(data, "serviceContent");
            info.ServiceEquity    = Str(data, "serviceEquity");
            info.BizMessage       = Str(data, "message");
            info.MaterialGroupName = Str(data, "materialGroupName");

            info.OldExchangeNewFlag   = Bool(data, "oldExchangeNewFlag");
            info.StopServiceFlag      = Bool(data, "stopServiceFlag");
            info.HardDiskMaterialFlag = Bool(data, "hardDiskMaterialFlag");
            info.NewBizFlag           = Bool(data, "newBizFlag");
            info.OemQueryShowFlag     = Bool(data, "oemQueryShowFlag");

            string daysRaw = Str(data, "timeToOutOfInsurance");
            int d;
            if (int.TryParse(daysRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out d))
            {
                info.Days = d;
                info.HasDays = true;
            }

            DateTime dt;
            if (DateTime.TryParseExact(info.WarrantyEndRaw, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out dt))
                info.WarrantyEnd = dt;
            else if (DateTime.TryParse(info.WarrantyEndRaw, CultureInfo.InvariantCulture, DateTimeStyles.None, out dt))
                info.WarrantyEnd = dt;

            if (!info.HasDays && info.WarrantyEnd.HasValue)
            {
                info.Days = (info.WarrantyEnd.Value.Date - DateTime.Today).Days;
                info.HasDays = true;
            }
        }

        static object Get(Dictionary<string, object> d, string key)
        {
            object v;
            return d.TryGetValue(key, out v) ? v : null;
        }

        static string Str(Dictionary<string, object> d, string key)
        {
            object v = Get(d, key);
            return v == null ? "" : Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        static bool Bool(Dictionary<string, object> d, string key)
        {
            object v = Get(d, key);
            if (v == null) return false;
            if (v is bool) return (bool)v;
            bool b;
            return bool.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), out b) && b;
        }

        static Dictionary<string, object> Obj(Dictionary<string, object> d, string key)
        {
            return Get(d, key) as Dictionary<string, object>;
        }

        static string DecodeUtf8(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return "";
            int offset = (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) ? 3 : 0;
            return Encoding.UTF8.GetString(bytes, offset, bytes.Length - offset);
        }

        static string JsonEscape(string s)
        {
            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"':  sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\r': sb.Append("\\r");  break;
                    case '\n': sb.Append("\\n");  break;
                    case '\t': sb.Append("\\t");  break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        public void Dispose()
        {
            _http.Dispose();
        }
    }

    /// <summary>批量并发查询调度：限制并发数、保证结果顺序、支持取消。</summary>
    public static class BatchRunner
    {
        public static List<WarrantyInfo> Run(WarrantyClient client, IList<string> serials, int concurrency,
                                             Action<WarrantyInfo> onOne, CancellationToken ct)
        {
            if (concurrency < 1) concurrency = 1;
            var results = new WarrantyInfo[serials.Count];
            var sem = new SemaphoreSlim(concurrency);
            var tasks = new Task[serials.Count];

            for (int i = 0; i < serials.Count; i++)
            {
                int idx = i;
                tasks[i] = Task.Run(async delegate
                {
                    WarrantyInfo info;
                    try
                    {
                        await sem.WaitAsync(ct).ConfigureAwait(false);
                        try { info = await client.QueryAsync(serials[idx], ct).ConfigureAwait(false); }
                        finally { sem.Release(); }
                    }
                    catch (OperationCanceledException) { info = new WarrantyInfo { Serial = serials[idx], Error = "已取消" }; }
                    catch (Exception ex) { info = new WarrantyInfo { Serial = serials[idx], Error = ex.Message }; }

                    results[idx] = info;
                    if (onOne != null) { try { onOne(info); } catch { } }
                }, ct);
            }

            try { Task.WaitAll(tasks); }
            catch (AggregateException) { }
            finally { sem.Dispose(); }

            var list = new List<WarrantyInfo>(serials.Count);
            foreach (var r in results) if (r != null) list.Add(r);
            return list;
        }
    }
}