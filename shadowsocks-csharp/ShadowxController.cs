using HtmlAgilityPack;
using Newtonsoft.Json;
using Shadowsocks.Model;
using Shadowsocks.View;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Cache;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Text;
using System.Threading.Tasks;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;

namespace Shadowsocks.Controller
{
    public class NewMenuViewController : MenuViewController
    {
        public static NewMenuViewController controller { get; private set; }
        public NewMenuViewController(ShadowsocksController controller) : base(controller)
        {
            NewMenuViewController.controller = this;
        }
        public void ShowBalloonTip(string message)
        {
            base.ShowBalloonTip("Shadowsocks", message, System.Windows.Forms.ToolTipIcon.Info, 1000);
        }
    }
    public class CleanConfigFlag
    {
        public object CleanConfig()
        {
            var config = Configuration.Load();
            config.configs = new List<Server>() { Configuration.GetDefaultServer() };
            config.index = -1;
            config.strategy = null;
            config.isDefault = false;
            config.autoCheckUpdate = false;
            config.proxy = null;
            Configuration.Save(config);
            return null;
        }
    }
    public class NewShadowsocksController : ShadowsocksController
    {
        private readonly object cleanconfigflag = new CleanConfigFlag().CleanConfig();

        private string host = "https://raw.githubusercontent.com/nicenigh/shadowsocks-windows/master/urls.txt";

        public new void Start()
        {
            GetServers();

            base.Start();
        }

        private Task GetServers()
        {
            Stopwatch watch = new Stopwatch();
            return Task.Factory.StartNew(() =>
           {
               ShowBalloonTip($"开始获取服务器列表");
               var urls = JsonConvert.DeserializeObject<List<string>>(BytesToString(Base64ToBytes(HttpGet(host).Result)));
               watch.Start();
               var imgs = GetImageFromPageSync(urls).GetAwaiter().GetResult();
               var servers = GetServerFromImageUrlSync(imgs).GetAwaiter().GetResult();
               watch.Stop();
               if (servers.Count > 0)
               {
                   var fast = PingCheck(servers.Select(en => en.server)).GetAwaiter().GetResult();
                   var config = base.GetCurrentConfiguration();
                   config.configs.Clear();
                   config.configs.AddRange(servers);
                   config.index = servers.IndexOf(servers.FirstOrDefault(en => en.server == fast));
                   base.SaveConfig(config);
               }
               ShowBalloonTip($"已加载 { servers.Count } / { imgs.Count } 个服务器" +
                   $"\n耗时{ watch.Elapsed.Minutes }分{ watch.Elapsed.Seconds }秒");
           });
        }

        private void ShowBalloonTip(string message)
        {
            NewMenuViewController.controller?.ShowBalloonTip(message);
        }

        private string imgbase64flag = "data:image/png;base64,";
        private async Task<List<string>> GetImageFromPageSync(IEnumerable<string> urls)
        {
            List<string> imageList = new List<string>();
            foreach (var url in urls)
            {
                try
                {
                    var html = await HttpGet(url);
                    if (string.IsNullOrEmpty(html)) return new List<string>();

                    var doc = new HtmlDocument();
                    doc.LoadHtml(html);

                    var hrefList = doc.DocumentNode.SelectNodes("//a[@href]")
                        .Select(en => en.Attributes["href"]?.Value)
                        .Select(en => en.StartsWith(imgbase64flag) ? en : (en.EndsWith(".png") && TryParseAbsUrl(url, ref en)) ? en : null)
                        .Where(en => !string.IsNullOrWhiteSpace(en));
                    imageList.AddRange(hrefList);
                }
                catch (Exception e)
                {
                }
            }
            return imageList;
        }

        private async Task<List<Server>> GetServerFromImageUrlSync(IEnumerable<string> urls)
        {
            List<Server> serverList = new List<Server>();
            foreach (var img in urls)
            {
                try
                {
                    byte[] bytes = null;
                    if (img.StartsWith("http"))
                        bytes = await HttpGetBytes(img);
                    else if (img.StartsWith(imgbase64flag))
                        bytes = Base64ToBytes(img.Substring(imgbase64flag.Length));
                    if (bytes != null && TryParseServer(QRDecode(bytes), out Server server))
                        serverList.Add(server);
                }
                catch (Exception e)
                {
                }
            }
            return serverList;
        }

        private bool TryParseAbsUrl(string host, ref string url)
        {
            Uri result = null;
            if (!Uri.TryCreate(url, UriKind.Absolute, out result))
                if (Uri.TryCreate(url, UriKind.Relative, out result))
                    if (!Uri.TryCreate(new Uri(host), result, out result))
                        result = null;

            if (result == null) return false;
            else url = result.AbsoluteUri;
            return true;
        }

        private RemoteCertificateValidationCallback ServerCertificateValidationCallback = (sender, cert, chain, error) => true;
        private async Task<byte[]> HttpGetBytes(string url)
        {
            ServicePointManager.ServerCertificateValidationCallback = ServerCertificateValidationCallback;
            HttpWebRequest request = (HttpWebRequest)HttpWebRequest.Create(url);
            request.Method = "GET";
            request.UserAgent = "Mozilla/5.0";
            request.CachePolicy = new RequestCachePolicy(RequestCacheLevel.NoCacheNoStore);
            request.Timeout = 2000;

            return await Task.Factory.StartNew(() =>
            {
                using (WebResponse response = request.GetResponse())
                using (Stream stream = response.GetResponseStream())
                {
                    byte[] bytes = new byte[response.ContentLength];
                    stream.Read(bytes, 0, (int)response.ContentLength);
                    return bytes;
                }
            });
        }

        private async Task<string> HttpGet(string url)
        {
            return BytesToString(await HttpGetBytes(url));
        }

        public byte[] Base64ToBytes(string basestr)
        {
            return Convert.FromBase64String(basestr);
        }

        public string BytesToString(byte[] bytes)
        {
            return Encoding.UTF8.GetString(bytes);
        }

        public string QRDecode(byte[] bytes)
        {
            using (MemoryStream ms = new MemoryStream(bytes))
            using (Bitmap bmp = new Bitmap(ms))
            {
                var source = new BitmapLuminanceSource(bmp);
                var bitmap = new BinaryBitmap(new HybridBinarizer(source));
                QRCodeReader reader = new QRCodeReader();
                var result = reader.decode(bitmap);
                return result?.Text;
            }
        }

        public bool TryParseServer(string ss, out Server server)
        {
            try
            {
                server = new Server(ss);
                return true;
            }
            catch
            {
                server = null;
                return false;
            }
        }

        private async Task<string> PingCheck(IEnumerable<string> hosts)
        {
            var delay = await Ping(hosts);
            var min = delay.Min(en => en.Value);
            return delay.FirstOrDefault(en => en.Value == min).Key;
        }

        private byte[] EmptyBytes = new byte[] { };
        public async Task<Dictionary<string, long>> Ping(IEnumerable<string> hosts)
        {
            int timeout = 2000;
            Dictionary<string, long> delay = new Dictionary<string, long>();
            foreach (var host in hosts)
            {
                delay.Add(host, await Task.Factory.StartNew(() =>
                {
                    Ping ping = new Ping();
                    var result = ping.Send(host, timeout, EmptyBytes);
                    if (result.Status == IPStatus.Success)
                        return result.RoundtripTime;
                    else
                        return timeout;
                }));
            }
            return delay;
        }


#if DEBUG
        #region 

        private void WaitEachTask<T>(IEnumerable<T> list, Action<T> action)
        {
            List<Task> tasks = new List<Task>();
            foreach (var item in list)
                tasks.Add(Task.Factory.StartNew(() =>
                {
                    try { action.Invoke(item); }
                    catch (Exception e)
                    {
                    }
                }));
            Task.WaitAll(tasks.ToArray());
        }

        private List<Server> GetServerList(string[] hosts)
        {
            List<Server> serverList = new List<Server>();
            List<Task> list = new List<Task>();
            foreach (string host in hosts)
            {
                list.Add(Task.Factory.StartNew(delegate
                {
                    try
                    {
                        string html = this.GetHtml(host);
                        HtmlDocument document = new HtmlDocument();
                        document.LoadHtml(html);
                        var imgs = document.DocumentNode.SelectNodes("//a[@href]")
                            .Select(en => en.Attributes["href"]?.Value)
                            .Where(en => !string.IsNullOrWhiteSpace(en) && (en.EndsWith(".png") || en.StartsWith("data:image/png;base64")));

                        List<Task> list2 = new List<Task>();
                        Uri uri = new Uri(host);
                        foreach (string img in imgs)
                        {
                            if (img.EndsWith(".png"))
                            {
                                string url = img;
                                if (TryParseAbsUrl(host, ref url))
                                {
                                    list2.Add(Task.Factory.StartNew(delegate
                                    {
                                        try
                                        {
                                            //string server = this.GetServer(url);
                                            //if (!string.IsNullOrEmpty(server) && Server.UrlFinder.IsMatch(server))
                                            //{
                                            //    serverList.Add(new Server(server));
                                            //}
                                        }
                                        catch (Exception e)
                                        {
                                        }
                                    }));
                                }
                            }
                            else if (img.StartsWith("data:image/png;base64"))
                            {
                                try
                                {
                                    string server = QRDecode(Base64ToBytes(img));
                                    if (!string.IsNullOrEmpty(server) && Server.UrlFinder.IsMatch(server))
                                    {
                                        serverList.Add(new Server(server));
                                    }
                                }
                                catch (Exception e)
                                {
                                }
                            }
                        }
                        Task.WaitAll(list2.ToArray());
                    }
                    catch (Exception e)
                    {
                    }
                }));
            }
            Task.WaitAll(list.ToArray());
            return serverList;
        }
        private string GetHtml(string url)
        {
            Uri uri;
            if (Uri.TryCreate(url, UriKind.Absolute, out uri))
            {
                try
                {
                    HttpWebRequest request = (HttpWebRequest)HttpWebRequest.Create(url);
                    request.Method = "GET";
                    request.UserAgent = "Mozilla/5.0";

                    using (WebResponse response = request.GetResponse())
                    using (StreamReader reader = new StreamReader(response.GetResponseStream()))
                        return reader.ReadToEnd();
                }
                catch (Exception e)
                {
                    throw e;
                }
            }
            else
            {
                throw new Exception("服务器URL格式不正确");
            }
        }

        private Bitmap GetImage(string url)
        {
            Uri uri;
            if (Uri.TryCreate(url, UriKind.Absolute, out uri))
            {
                try
                {
                    HttpWebRequest request = (HttpWebRequest)HttpWebRequest.Create(url);
                    request.Method = "GET";
                    request.UserAgent = "Mozilla/5.0";

                    using (WebResponse response = request.GetResponse())
                    using (Stream stream = response.GetResponseStream())
                    using (Image img = Image.FromStream(stream))
                    using (MemoryStream ms = new MemoryStream())
                    {
                        img.Save(ms, System.Drawing.Imaging.ImageFormat.Jpeg);
                        return new Bitmap(ms);
                    }
                }
                catch (Exception e)
                {
                    throw e;
                }
            }
            else
            {
                throw new Exception("图片URL格式不正确");
            }
        }
        private string GetAbsoluteUrl(Uri host, string url)
        {
            Uri result = null;
            if (Uri.TryCreate(url, UriKind.Absolute, out result)) { }
            else if (Uri.TryCreate(url, UriKind.Relative, out result))
                if (Uri.TryCreate(host, result, out result)) { }
                else result = null;

            return result?.AbsoluteUri ?? throw new Exception("URL格式不正确");
        }
        #endregion
#endif
    }
}
