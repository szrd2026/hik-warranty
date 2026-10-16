using System;
using System.Collections.Generic;
using System.Globalization;

namespace HikWarranty
{
    /// <summary>单台设备的维保查询结果（同时承载未查询到 / 网络失败等信息）。</summary>
    public sealed class WarrantyInfo
    {
        public string Serial = "";            // 序列号（统一大写）
        public bool Ok;                       // 接口是否返回了有效数据
        public string Code = "";              // 接口 code：000 = 成功
        public string Message = "";           // 接口 msg
        public string Error = "";             // 本地网络 / 超时 / 解析错误

        public string Model = "";             // 产品型号 labelModel
        public string WarrantyEndRaw = "";    // 保修截止（原始 yyyyMMdd）
        public DateTime? WarrantyEnd;         // 保修截止（解析后）
        public int Days;                      // 剩余天数（负数 = 已过保）
        public bool HasDays;

        public string ServiceLevel = "";      // 服务等级编号
        public string ServiceLevelName = "";  // 服务等级名称
        public string ServiceLevelMsg = "";   // 服务等级说明
        public string ImageUrl = "";          // 产品图片地址
        public string ProductType = "";
        public string OemQueryState = "";     // OEM 查询状态
        public string ConfigDesc = "";        // 配置说明
        public string ServiceContent = "";    // 服务内容
        public string ServiceEquity = "";     // 服务权益
        public string BizMessage = "";        // data.message，接口的业务提示
        public string MaterialGroupName = ""; // 物料组

        public bool OldExchangeNewFlag;       // 以旧换新
        public bool StopServiceFlag;          // 停止服务
        public bool HardDiskMaterialFlag;     // 硬盘物料
        public bool NewBizFlag;               // 新业务标识
        public bool OemQueryShowFlag;

        public long ElapsedMs;                // 单条请求耗时
        public string RawJson = "";           // 原始响应（详情页展示）

        /// <summary>是否已过保（优先用接口给出的剩余天数）。</summary>
        public bool Expired
        {
            get
            {
                if (HasDays) return Days < 0;
                if (WarrantyEnd.HasValue) return WarrantyEnd.Value.Date < DateTime.Today;
                return false;
            }
        }

        public string EndDateDisplay
        {
            get
            {
                if (WarrantyEnd.HasValue)
                    return WarrantyEnd.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                return WarrantyEndRaw;
            }
        }

        public string StatusText
        {
            get
            {
                if (Error.Length > 0) return "失败";
                if (!Ok) return "未查到";
                if (HasDays || WarrantyEnd.HasValue) return Expired ? "已过保" : "在保";
                return "—";
            }
        }

        /// <summary>列表“说明”列：优先业务提示，其次本地错误，最后服务说明。</summary>
        public string Note
        {
            get
            {
                if (Error.Length > 0) return Error;
                if (BizMessage.Length > 0) return BizMessage;
                return ServiceLevelMsg;
            }
        }

        public string DaysDisplay
        {
            get { return HasDays ? Days.ToString(CultureInfo.InvariantCulture) : ""; }
        }
    }

    /// <summary>序列号文本解析：兼容换行、空格、逗号、分号、制表符分隔。</summary>
    public static class SnParser
    {
        static readonly char[] Separators = new char[] { '\r', '\n', ',', ';', '，', '；', '\t', ' ' };

        public static List<string> Parse(string text)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(text)) return list;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string part in text.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
            {
                string sn = part.Trim().ToUpperInvariant();
                if (sn.Length > 0 && seen.Add(sn)) list.Add(sn);
            }
            return list;
        }
    }
}