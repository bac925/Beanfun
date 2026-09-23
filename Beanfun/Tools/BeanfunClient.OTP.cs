using System;
using System.Collections.Specialized;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Beanfun
{
    public partial class BeanfunClient : WebClient
    {
        /*
         * GGM Command.DecryptParam substitution tables.
         * mode = firstHexNibble % 4
         */
        private static readonly Dictionary<int, string> GgmHexTables =
            new Dictionary<int, string>
            {
                { 0, "bac987d65e432f10" },
                { 1, "3bc4d5e6f2a79108" },
                { 2, "cdbeaf9012456378" },
                { 3, "4e6fb81a3c5d7092" },
            };

        private sealed class GgmLaunchData
        {
            public int Mode { get; set; }
            public string DesKey { get; set; }
            public string LaunchTicket { get; set; }
            public string ServiceCode { get; set; }
            public string ServiceRegion { get; set; }
            public string ServiceAccount { get; set; }
            public string BeanfunUrl { get; set; }
            public string WebStartPatch { get; set; }
            public string Extra { get; set; }
            public Dictionary<string, string> Args { get; set; }
        }

        public string GetOTP(
            ServiceAccount acc,
            string service_code = "610074",
            string service_region = "T9"
        )
        {
            /*
             * TW changed to GGM / get_webstart_otp_v2.
             * Keep the old implementation for non-TW regions.
             */
            if (App.LoginRegion != "TW")
                return GetOTPLegacy(acc, service_code, service_region);

            return GetOTPV2(acc, service_code, service_region);
        }

        private string GetOTPV2(
            ServiceAccount acc,
            string service_code,
            string service_region
        )
        {
            try
            {
                if (acc == null)
                {
                    this.errmsg = "OTPNoAccount";
                    return null;
                }

                if (!GGMRuntimeInfo.EnsureInitialized())
                {
                    this.errmsg =
                        "找不到或無法讀取 gamania Games Manager。\r\n\r\n" +
                        (GGMRuntimeInfo.LastError ?? "") +
                        "\r\n\r\n預設位置：\r\n" +
                        @"C:\Program Files\gamania Games\gamania Games Manager";
                    return null;
                }

                string host = "tw.beanfun.com";

                // ------------------------------------------------------------
                // 1. game_start_step2
                // ------------------------------------------------------------
                string step2Url =
                    $"https://{host}/beanfun_block/game_zone/game_start_step2.aspx" +
                    $"?service_code={Uri.EscapeDataString(service_code)}" +
                    $"&service_region={Uri.EscapeDataString(service_region)}" +
                    $"&sotp={Uri.EscapeDataString(acc.ssn)}" +
                    $"&dt={Uri.EscapeDataString(GetCurrentTime(2))}";

                string html = this.DownloadString(step2Url);
                if (string.IsNullOrWhiteSpace(html))
                {
                    this.errmsg = "OTPNoStep2Response";
                    return null;
                }

                // Keep ServiceAccountCreateTime synchronized with the actual step2 page.
                string createTime = ExtractAccountValue(html, "ServiceAccountCreateTime");
                if (!string.IsNullOrWhiteSpace(createTime))
                    acc.screatetime = createTime;

                if (string.IsNullOrWhiteSpace(acc.screatetime))
                {
                    this.errmsg = "OTPNoCreateTime";
                    return null;
                }

                string accountId = ExtractAccountValue(html, "ServiceAccountID");
                string accountSn = ExtractAccountValue(html, "ServiceAccountSN");
                string displayName = ExtractAccountValue(html, "ServiceAccountDisplayName");

                if (string.IsNullOrWhiteSpace(accountId))
                    accountId = acc.sid;

                if (string.IsNullOrWhiteSpace(accountSn))
                    accountSn = acc.ssn;

                if (string.IsNullOrWhiteSpace(displayName))
                    displayName = acc.sname;

                string ggmSn = ExtractJsonLikeValue(html, "sn", "m_objData");
                string ggmData = ExtractJsonLikeValue(html, "data", "m_objData");

                if (string.IsNullOrWhiteSpace(ggmSn))
                {
                    this.errmsg = "OTPNoGgmSN";
                    return null;
                }

                if (string.IsNullOrWhiteSpace(ggmData))
                {
                    this.errmsg = "OTPNoGgmData";
                    return null;
                }

                // ------------------------------------------------------------
                // 2. record_service_start
                // ------------------------------------------------------------
                NameValueCollection payload = new NameValueCollection();
                payload.Add("service_code", service_code);
                payload.Add("service_region", service_region);
                payload.Add("service_account_id", accountId);
                payload.Add("sotp", accountSn);
                payload.Add("service_account_display_name", displayName ?? "");
                payload.Add("service_account_create_time", acc.screatetime);

                // New pages may append an anti-CSRF style dynamic field to strFormData.
                // It is accepted when present, but is not required by every deployment.
                TryAppendRecordServiceDynamicField(html, payload);

                ServicePointManager.Expect100Continue = false;
                this.UploadString(
                    $"https://{host}/beanfun_block/generic_handlers/record_service_start.ashx",
                    payload
                );

                // ------------------------------------------------------------
                // 3. Decode GGM Cmd=06006 Data -> LaunchTicket
                // ------------------------------------------------------------
                GgmLaunchData launch = DecodeGgm06006Data(ggmData);

                if (launch == null || string.IsNullOrWhiteSpace(launch.LaunchTicket))
                {
                    this.errmsg = "OTPNoLaunchTicket";
                    return null;
                }

                // ------------------------------------------------------------
                // 4. get_webstart_otp_v2
                // ------------------------------------------------------------
                JObject requestJson = new JObject
                {
                    ["SN"] = ggmSn,
                    ["LaunchTicket"] = launch.LaunchTicket,
                    ["CV"] = GGMRuntimeInfo.Version,
                    ["Hash"] = GGMRuntimeInfo.Hash,
                    ["arch"] = GGMRuntimeInfo.Arch,
                };

                string response = PostOtpV2(
                    "https://tw.beanfun.com/beanfun_block/generic_handlers/get_webstart_otp_v2.ashx",
                    requestJson.ToString(Formatting.None)
                );

                if (string.IsNullOrWhiteSpace(response))
                {
                    this.errmsg = "OTPNoResponse";
                    return null;
                }

                JObject json;
                try
                {
                    json = JObject.Parse(response);
                }
                catch
                {
                    this.errmsg = "OTPInvalidV2Response:" + response;
                    return null;
                }

                int result = json["result"]?.Value<int>() ?? 0;
                string encryptedData = json["data"]?.ToString() ?? "";
                string message = json["message"]?.ToString() ?? "";

                if (result != 1)
                {
                    /*
                     * 保留已知的官方錯誤碼，不再先加上 GetOtpError 前綴。
                     * MainWindow 會依錯誤碼提供對使用者更明確的處理方式。
                     */
                    if (
                        !string.IsNullOrWhiteSpace(message)
                        && message.IndexOf(
                            "Client_Integrity_Failed",
                            StringComparison.OrdinalIgnoreCase
                        ) >= 0
                    )
                    {
                        this.errmsg = "Client_Integrity_Failed";
                    }
                    else if (
                        !string.IsNullOrWhiteSpace(message)
                        && message.IndexOf(
                            "OTPNoGgmSN",
                            StringComparison.OrdinalIgnoreCase
                        ) >= 0
                    )
                    {
                        this.errmsg = "OTPNoGgmSN";
                    }
                    else
                    {
                        this.errmsg =
                            string.IsNullOrWhiteSpace(message)
                                ? $"OTP V2 result={result}"
                                : message;
                    }

                    return null;
                }

                if (encryptedData.Length < 9)
                {
                    this.errmsg = "OTPInvalidV2Data";
                    return null;
                }

                string key = encryptedData.Substring(0, 8);
                string encryptedHex = encryptedData.Substring(8);

                string otp = WCDESComp.DecryStrHex(encryptedHex, key);
                if (otp == null)
                {
                    this.errmsg = "DecryptOTPError";
                    return null;
                }

                otp = otp.Trim('\0');
                this.errmsg = null;
                return otp;
            }
            catch (Exception e)
            {
                this.errmsg =
                    (System.Windows.Application.Current.TryFindResource("GetOtpError") as string)
                    + "\n\n"
                    + e.Message
                    + "\n"
                    + e.StackTrace;
                return null;
            }
        }

        /// <summary>
        /// Official GGM H.a() uses a fresh HttpClient and application/json.
        /// It does not reuse the beanfun WebClient CookieContainer.
        /// </summary>
        private static string PostOtpV2(string url, string json)
        {
            using (HttpClient client = new HttpClient())
            using (StringContent content = new StringContent(json, Encoding.UTF8, "application/json"))
            {
                client.Timeout = TimeSpan.FromSeconds(30);

                HttpResponseMessage response =
                    client.PostAsync(url, content).GetAwaiter().GetResult();

                return response.Content
                    .ReadAsStringAsync()
                    .GetAwaiter()
                    .GetResult();
            }
        }

        private static GgmLaunchData DecodeGgm06006Data(string data)
        {
            data = (data ?? "").Trim();
            if (data.Length < 2)
                throw new Exception("GGM Data 長度不足");

            int num;
            if (!int.TryParse(
                    data.Substring(0, 1),
                    System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out num
                ))
            {
                throw new Exception("GGM Data 第一碼不是 hex");
            }

            int mode = num % 4;
            string table;
            if (!GgmHexTables.TryGetValue(mode, out table))
                throw new Exception($"未知 GGM mode={mode}");

            string body = data.Substring(1);
            StringBuilder decodedHexBuilder = new StringBuilder(body.Length);

            foreach (char c in body)
            {
                int index = table.IndexOf(c);
                if (index < 0)
                    throw new Exception($"GGM mode={mode} 無法解析字元 '{c}'");

                decodedHexBuilder.Append(index.ToString("x"));
            }

            string decodedHex = decodedHexBuilder.ToString();

            int keyPosition = num + 1;
            if (decodedHex.Length < keyPosition + 8)
                throw new Exception("GGM Data 無法取得 DES key");

            string desKey = decodedHex.Substring(keyPosition, 8);

            string encryptedHex =
                decodedHex.Substring(0, keyPosition)
                + decodedHex.Substring(keyPosition + 8);

            string plaintext = WCDESComp.DecryStrHex(encryptedHex, desKey);
            if (plaintext == null)
                throw new Exception("GGM 06006 DES 解密失敗");

            plaintext = plaintext.TrimEnd('\0');

            string[] sections = plaintext.Split(';');
            string query = sections.Length > 0 ? sections[0] : "";

            Dictionary<string, string> args =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (
                string item in query.Split(
                    new[] { '&' },
                    StringSplitOptions.RemoveEmptyEntries
                )
            )
            {
                int eq = item.IndexOf('=');
                if (eq <= 0)
                    continue;

                string key = item.Substring(0, eq);
                string value = item.Substring(eq + 1);

                if (!args.ContainsKey(key))
                    args.Add(key, value);
            }

            string Get(string name)
            {
                string value;
                return args.TryGetValue(name, out value) ? value : "";
            }

            return new GgmLaunchData
            {
                Mode = mode,
                DesKey = desKey,
                LaunchTicket = Get("LaunchTicket"),
                ServiceCode = Get("ServiceCode"),
                ServiceRegion = Get("ServiceRegion"),
                ServiceAccount = Get("ServiceAccount"),
                BeanfunUrl = Get("BeanfunUrl"),
                WebStartPatch = Get("WebStartPatch"),
                Extra = sections.Length > 1 ? string.Join(";", sections, 1, sections.Length - 1) : "",
                Args = args,
            };
        }

        private static string ExtractAccountValue(string html, string property)
        {
            if (string.IsNullOrWhiteSpace(html))
                return "";

            Match match = Regex.Match(
                html,
                @"\b" + Regex.Escape(property) + @"\s*:\s*[""']([^""']*)[""']",
                RegexOptions.IgnoreCase
            );

            return match.Success
                ? WebUtility.HtmlDecode(match.Groups[1].Value)
                : "";
        }

        private static string ExtractJsonLikeValue(
            string html,
            string property,
            string objectName
        )
        {
            if (string.IsNullOrWhiteSpace(html))
                return "";

            Match objectMatch = Regex.Match(
                html,
                @"var\s+" + Regex.Escape(objectName) + @"\s*=\s*\{([\s\S]*?)\}\s*;",
                RegexOptions.IgnoreCase
            );

            if (!objectMatch.Success)
                return "";

            Match valueMatch = Regex.Match(
                objectMatch.Groups[1].Value,
                @"[""']?" + Regex.Escape(property) + @"[""']?\s*:\s*[""']([^""']*)[""']",
                RegexOptions.IgnoreCase
            );

            return valueMatch.Success
                ? WebUtility.HtmlDecode(valueMatch.Groups[1].Value)
                : "";
        }

        private static void TryAppendRecordServiceDynamicField(
            string html,
            NameValueCollection payload
        )
        {
            try
            {
                Match match = Regex.Match(
                    html,
                    @"service_account_create_time[^;]*?&([^=&""']+)=([^&""']+)",
                    RegexOptions.IgnoreCase
                );

                if (!match.Success)
                    return;

                string key = Uri.UnescapeDataString(match.Groups[1].Value);
                string value = Uri.UnescapeDataString(match.Groups[2].Value);

                if (
                    !string.IsNullOrWhiteSpace(key)
                    && payload[key] == null
                )
                {
                    payload.Add(key, value);
                }
            }
            catch
            {
                // Optional field; ignore parse failures.
            }
        }

        // ====================================================================
        // Legacy flow retained for HK / older regions
        // ====================================================================

        private string GetOTPLegacy(
            ServiceAccount acc,
            string service_code,
            string service_region
        )
        {
            try
            {
                string response;
                string host;
                string loginHost;

                if (App.LoginRegion == "TW")
                {
                    host = "tw.beanfun.com";
                    loginHost = "tw.newlogin.beanfun.com";
                }
                else
                {
                    host = "bfweb.hk.beanfun.com";
                    loginHost = "login.hk.beanfun.com";
                }

                response = this.DownloadString(
                    $"https://{host}/beanfun_block/game_zone/game_start_step2.aspx?service_code={service_code}&service_region={service_region}&sotp={acc.ssn}&dt={GetCurrentTime(2)}"
                );

                Regex regex = new Regex("GetResultByLongPolling&key=(.*)\"");
                if (!regex.IsMatch(response))
                {
                    this.errmsg = "OTPNoLongPollingKey:" + response;
                    return null;
                }

                string longPollingKey = regex.Match(response).Groups[1].Value;
                string unkKey = null;
                string unkValue = null;

                if (App.LoginRegion == "TW")
                {
                    regex = new Regex("MyAccountData.ServiceAccountCreateTime \\+ \"(.*)=(.*)\";");
                    if (!regex.IsMatch(response))
                    {
                        this.errmsg = "OTPNoUnkData";
                        return null;
                    }

                    unkKey = Uri.UnescapeDataString(regex.Match(response).Groups[1].Value);
                    unkValue = Uri.UnescapeDataString(regex.Match(response).Groups[2].Value);
                }

                if (acc.screatetime == null)
                {
                    regex = new Regex("ServiceAccountCreateTime: \"([^\"]+)\"");
                    if (!regex.IsMatch(response))
                    {
                        this.errmsg = "OTPNoCreateTime";
                        return null;
                    }

                    acc.screatetime = regex.Match(response).Groups[1].Value;
                }

                response = this.DownloadString(
                    $"https://{loginHost}/generic_handlers/get_cookies.ashx"
                );

                regex = new Regex("var m_strSecretCode = '(.*)';");
                if (!regex.IsMatch(response))
                {
                    this.errmsg = "OTPNoSecretCode";
                    return null;
                }

                string secretCode = regex.Match(response).Groups[1].Value;

                NameValueCollection payload = new NameValueCollection();
                payload.Add("service_code", service_code);
                payload.Add("service_region", service_region);
                payload.Add("service_account_id", acc.sid);
                payload.Add("sotp", acc.ssn);
                payload.Add("service_account_display_name", acc.sname);
                payload.Add("service_account_create_time", acc.screatetime);

                if (unkKey != null && unkValue != null)
                    payload.Add(unkKey, unkValue);

                ServicePointManager.Expect100Continue = false;

                this.UploadString(
                    $"https://{host}/beanfun_block/generic_handlers/record_service_start.ashx",
                    payload
                );

                response = this.DownloadString(
                    $"https://{host}/generic_handlers/get_result.ashx?meth=GetResultByLongPolling&key={longPollingKey}&_={GetCurrentTime()}"
                );

                response = this.DownloadString(
                    $"https://{host}/beanfun_block/generic_handlers/get_webstart_otp.ashx?SN={longPollingKey}&WebToken={this.WebToken}&SecretCode={secretCode}&ppppp=1F552AEAFF976018F942B13690C990F60ED01510DDF89165F1658CCE7BC21DBA&ServiceCode={service_code}&ServiceRegion={service_region}&ServiceAccount={acc.sid}&CreateTime={acc.screatetime.Replace(" ", "%20")}&d={Environment.TickCount}"
                );

                if (string.IsNullOrEmpty(response))
                {
                    this.errmsg = "OTPNoResponse";
                    return null;
                }

                string[] responses = response.Split(';');
                if (responses.Length < 2)
                {
                    this.errmsg = "OTPNoResponse";
                    return null;
                }

                response = responses[1];

                if (responses[0] != "1")
                {
                    this.errmsg =
                        (
                            System.Windows.Application.Current.TryFindResource("GetOtpError")
                            as string
                        )
                        + "\r\n"
                        + response;
                    return null;
                }

                string key = response.Substring(0, 8);
                string plain = response.Substring(8);
                string otp = WCDESComp.DecryStrHex(plain, key);

                if (otp != null)
                {
                    otp = otp.Trim('\0');
                    this.errmsg = null;
                }
                else
                {
                    this.errmsg = "DecryptOTPError";
                }

                return otp;
            }
            catch (Exception e)
            {
                this.errmsg =
                    (System.Windows.Application.Current.TryFindResource("GetOtpError") as string)
                    + "\n\n"
                    + e.Message
                    + "\n"
                    + e.StackTrace;
                return null;
            }
        }
    }
}
