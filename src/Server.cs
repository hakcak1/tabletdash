// TabletDash: PC sicakliklari (HWiNFO) + iRacing telemetrisini eski bir tablete web sayfasi olarak sunar.
// .NET Framework 4 ile gelen csc.exe (C# 5) ile derlenir; ek kurulum gerekmez.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace TabletDash
{
    static class Program
    {
        static volatile string pcJson = "{\"hw\":false}";
        static volatile string raceJson = "{\"connected\":false}";
        static volatile string sensorDump = "No HWiNFO data yet.";
        static string wwwDir;
        static int port = 8080;

        static void Main(string[] args)
        {
            if (args.Length > 0) int.TryParse(args[0], out port);
            wwwDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "www"));
            try { Console.OutputEncoding = Encoding.UTF8; Console.Title = "TabletDash"; } catch { }

            StartThread(PcLoop);
            StartThread(RaceLoop);

            TcpListener listener = new TcpListener(IPAddress.Any, port);
            try { listener.Start(); }
            catch (SocketException ex)
            {
                Console.WriteLine("Could not open port " + port + ": " + ex.Message);
                Console.WriteLine("Another TabletDash may already be running.");
                Thread.Sleep(8000);
                return;
            }

            Console.WriteLine("TabletDash is running. Open one of these addresses in the tablet's browser:");
            foreach (string ip in LocalIPs()) Console.WriteLine("    http://" + ip + ":" + port + "/");
            Console.WriteLine("Sensor list: http://localhost:" + port + "/sensors");
            Console.WriteLine("Close this window to stop.");

            while (true)
            {
                TcpClient client = listener.AcceptTcpClient();
                Thread t = new Thread(HandleClient);
                t.IsBackground = true;
                t.Start(client);
            }
        }

        static void StartThread(ThreadStart fn)
        {
            Thread t = new Thread(fn);
            t.IsBackground = true;
            t.Start();
        }

        static List<string> LocalIPs()
        {
            List<string> ips = new List<string>();
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (UnicastIPAddressInformation a in ni.GetIPProperties().UnicastAddresses)
                {
                    if (a.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    string s = a.Address.ToString();
                    if (!s.StartsWith("169.254.")) ips.Add(s);
                }
            }
            return ips;
        }

        // ---------------------------------------------------------------- HTTP

        static string Snapshot()
        {
            return "{\"pc\":" + pcJson + ",\"race\":" + raceJson + "}";
        }

        // Keep-alive: tablet ayni TCP baglantisi uzerinden arka arkaya /data ister.
        // Uzun omurlu tek akis yerine kisa istekler, Wi-Fi paket kaybinda saniyelerce donmayi onler.
        static void HandleClient(object state)
        {
            TcpClient client = (TcpClient)state;
            try
            {
                client.NoDelay = true;
                NetworkStream ns = client.GetStream();
                ns.ReadTimeout = 20000;
                while (true)
                {
                    string path = ReadRequestPath(ns);
                    if (path == null) return;
                    int q = path.IndexOf('?');
                    if (q >= 0) path = path.Substring(0, q);

                    if (path == "/stream") { StreamEvents(ns); return; }
                    if (path == "/data") Send(ns, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(Snapshot()));
                    else if (path == "/sensors") Send(ns, "200 OK", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(sensorDump));
                    else ServeFile(ns, path);
                }
            }
            catch (Exception) { }
            finally { client.Close(); }
        }

        static string ReadRequestPath(NetworkStream ns)
        {
            byte[] buf = new byte[8192];
            int len = 0;
            string head = "";
            while (len < buf.Length)
            {
                int n = ns.Read(buf, len, buf.Length - len);
                if (n <= 0) break;
                len += n;
                head = Encoding.ASCII.GetString(buf, 0, len);
                if (head.Contains("\r\n\r\n")) break;
            }
            int eol = head.IndexOf("\r\n");
            if (eol < 0) return null;
            string[] parts = head.Substring(0, eol).Split(' ');
            if (parts.Length < 2 || parts[0] != "GET") return null;
            return parts[1];
        }

        // Server-Sent Events: ~30 Hz. Eski tarayicilar ilk 2 KB'i tamponladigi icin bosluk dolgusu gonderilir.
        static void StreamEvents(NetworkStream ns)
        {
            Write(ns, "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-cache\r\n" +
                      "Connection: keep-alive\r\nAccess-Control-Allow-Origin: *\r\n\r\n");
            Write(ns, ":" + new string(' ', 2048) + "\nretry: 1000\n\n");
            string last = null;
            int idle = 0;
            while (true)
            {
                string snap = Snapshot();
                if (snap != last || ++idle >= 30)
                {
                    Write(ns, "data: " + snap + "\n\n");
                    last = snap;
                    idle = 0;
                }
                Thread.Sleep(33);
            }
        }

        static void ServeFile(NetworkStream ns, string path)
        {
            if (path == "/") path = "/index.html";
            string name = Uri.UnescapeDataString(path.TrimStart('/')).Replace('/', Path.DirectorySeparatorChar);
            string full = Path.GetFullPath(Path.Combine(wwwDir, name));
            if (!full.StartsWith(wwwDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            {
                Send(ns, "404 Not Found", "text/plain", Encoding.ASCII.GetBytes("404"));
                return;
            }
            Send(ns, "200 OK", ContentType(full), File.ReadAllBytes(full));
        }

        static string ContentType(string file)
        {
            switch (Path.GetExtension(file).ToLowerInvariant())
            {
                case ".html": return "text/html; charset=utf-8";
                case ".js": return "application/javascript; charset=utf-8";
                case ".css": return "text/css; charset=utf-8";
                case ".png": return "image/png";
                case ".svg": return "image/svg+xml";
                case ".json": return "application/json; charset=utf-8";
                default: return "application/octet-stream";
            }
        }

        static void Send(NetworkStream ns, string status, string type, byte[] body)
        {
            Write(ns, "HTTP/1.1 " + status + "\r\nContent-Type: " + type + "\r\nContent-Length: " + body.Length +
                      "\r\nCache-Control: no-cache, no-store\r\nAccess-Control-Allow-Origin: *\r\nConnection: keep-alive\r\n\r\n");
            ns.Write(body, 0, body.Length);
            ns.Flush();
        }

        static void Write(NetworkStream ns, string s)
        {
            byte[] b = Encoding.UTF8.GetBytes(s);
            ns.Write(b, 0, b.Length);
            ns.Flush();
        }

        // ---------------------------------------------------------------- PC sensorleri (HWiNFO)

        const string GpuSensor = "NVIDIA|GeForce|Radeon RX|Radeon Pro|Arc";

        static void PcLoop()
        {
            HwInfo hw = new HwInfo();
            while (true)
            {
                try
                {
                    List<Reading> r = hw.Read();
                    if (r == null)
                    {
                        pcJson = "{\"hw\":false}";
                        sensorDump = "HWiNFO not found. Run HWiNFO with 'Shared Memory Support' enabled.";
                    }
                    else
                    {
                        pcJson = BuildPc(r);
                        sensorDump = Dump(r);
                    }
                }
                catch (Exception ex)
                {
                    hw.Close();
                    pcJson = new Json().Bool("hw", false).Str("err", ex.Message).ToString();
                }
                Thread.Sleep(500);
            }
        }

        static string BuildPc(List<Reading> rs)
        {
            Json j = new Json().Bool("hw", true);
            j.Num("cpuTemp", V(Find(rs, 1, null, "^CPU \\(Tctl/Tdie\\)", "^CPU Die \\(average\\)", "^CPU Package$", "^CPU Package", "^CPU$")), 1);
            j.Num("cpuLoad", V(Find(rs, 7, null, "^Total CPU Usage")), 0);
            j.Num("cpuPower", V(Find(rs, 5, null, "^CPU Package Power", "^CPU PPT$", "^CPU Core Power")), 0);
            j.Num("cpuClock", MaxCoreClock(rs), 0);
            j.Num("gpuTemp", V(Find(rs, 1, GpuSensor, "^GPU Temperature$", "^GPU Temperature")), 1);
            j.Num("gpuHot", V(Find(rs, 1, GpuSensor, "^GPU Hot ?Spot")), 1);
            j.Num("gpuMemTemp", V(Find(rs, 1, GpuSensor, "^GPU Memory Junction")), 1);
            j.Num("gpuLoad", V(Find(rs, 7, GpuSensor, "^GPU Core Load", "^GPU Utilization", "^GPU D3D Usage")), 0);
            j.Num("gpuPower", V(Find(rs, 5, GpuSensor, "^GPU Power$", "^Total GPU Power", "^GPU Power")), 0);
            j.Num("gpuClock", V(Find(rs, 6, GpuSensor, "^GPU Clock$", "^GPU Saati$", "^GPU Clock")), 0);
            j.Num("gpuMemClock", V(Find(rs, 6, GpuSensor, "^GPU Memory Clock$", "^GPU Bellek Saati$", "^GPU Memory Clock")), 0);
            j.Num("gpuVolt", V(Find(rs, 2, GpuSensor, "^GPU Core Voltage", "^GPU Çekirdek Voltajı")), 3);
            j.Num("gpuPowerPct", V(Find(rs, 8, GpuSensor, "^Total GPU Power \\[% of TDP\\]", "^Toplam GPU Gücü \\[% of TDP\\]")), 0);
            j.Num("gpuFan", V(Find(rs, 3, GpuSensor, "^GPU Fan")), 0);
            j.Num("vramUsed", V(Find(rs, 8, GpuSensor, "^GPU Memory Allocated", "^Ayrılan GPU Belleği", "^GPU D3D Memory Dedicated")), 0);
            j.Num("vramFree", V(Find(rs, 8, GpuSensor, "^GPU Memory Available")), 0);
            j.Num("vramLoad", V(Find(rs, 7, GpuSensor, "^GPU Memory Usage", "^GPU Bellek Kullanımı")), 0);
            j.Num("ramLoad", V(Find(rs, -1, null, "^Physical Memory Load", "^Fiziksel Bellek Yükü")), 0);
            AddFps(j, rs);
            j.Num("ramUsed", V(Find(rs, 8, null, "^Physical Memory Used")), 0);
            j.Num("ramFree", V(Find(rs, 8, null, "^Physical Memory Available")), 0);
            j.Num("ramTotal", InstalledRamMb(), 0);
            j.Num("ssdTemp", V(Find(rs, 1, "S\\.M\\.A\\.R\\.T|Drive|NVMe|SSD", "^Drive Temperature$", "^Drive Temperature")), 0);

            StringBuilder fans = new StringBuilder("[");
            int nf = 0;
            foreach (Reading r in rs)
            {
                if (r.Type != 3 || r.Value <= 0 || Regex.IsMatch(r.Sensor, GpuSensor, RegexOptions.IgnoreCase)) continue;
                if (nf > 0) fans.Append(',');
                fans.Append(new Json().Str("n", r.Label).Num("v", r.Value, 0).ToString());
                if (++nf >= 4) break;
            }
            j.Raw("fans", fans.Append(']').ToString());
            return j.ToString();
        }

        // FPS: once RTSS (oyun acikken dolu), yoksa HWiNFO'nun PresentMon sensoru (on plandaki uygulama).
        static void AddFps(Json j, List<Reading> rs)
        {
            Reading fps = Find(rs, -1, "^RTSS$", "^Framerate$", "^Kare Hızı$");
            Reading low = Find(rs, -1, "^RTSS$", "^Framerate 1% Low", "^Kare Hızı 1% Düşük");
            Reading ft = Find(rs, -1, "^RTSS$", "^Frame ?time", "^Çerçeve Süresi");
            Reading pm = Find(rs, -1, "^PresentMon", "Framerate Presented", "Kare Hızı Presented", "Framerate", "Kare Hızı");
            string app = "";
            if (pm != null)
            {
                Match m = Regex.Match(pm.Sensor, "\\[(.+?)\\]");
                if (m.Success) app = Regex.Replace(m.Groups[1].Value, "\\.exe$", "", RegexOptions.IgnoreCase);
            }
            if (fps == null || !(fps.Value > 0))
            {
                fps = pm;
                low = Find(rs, -1, "^PresentMon", "Framerate Presented \\(1% low\\)", "Kare Hızı Presented \\(1% low\\)");
                ft = Find(rs, -1, "^PresentMon", "Frame ?Time", "Çerçeve Süresi");
            }
            j.Num("fps", V(fps), 0).Num("fpsLow", V(low), 0).Num("frameTime", V(ft), 1).Str("fpsApp", app);
        }

        static double MaxCoreClock(List<Reading> rs)
        {
            double max = double.NaN;
            foreach (Reading r in rs)
                if (r.Type == 6 && Regex.IsMatch(r.LabelOrig, "^Core \\d+ (Clock|Saati)", RegexOptions.IgnoreCase) && !(r.Value <= max))
                    max = r.Value;
            return max;
        }

        static Reading Find(List<Reading> rs, int type, string sensorPat, params string[] labelPats)
        {
            foreach (string lp in labelPats)
            {
                Regex re = new Regex(lp, RegexOptions.IgnoreCase);
                foreach (Reading r in rs)
                {
                    if (type >= 0 && r.Type != type) continue;
                    if (sensorPat != null && !Regex.IsMatch(r.Sensor, sensorPat, RegexOptions.IgnoreCase)) continue;
                    if (re.IsMatch(r.Label) || re.IsMatch(r.LabelOrig)) return r;
                }
            }
            return null;
        }

        static double V(Reading r) { return r == null ? double.NaN : r.Value; }

        [DllImport("kernel32.dll")]
        static extern bool GetPhysicallyInstalledSystemMemory(out long totalKb);

        // Takili RAM (orn. 32 GB). "Kullanilan + bos" degeri donanim ayrilmasi yuzunden ~31 GB gorunur.
        static double installedRamMb = -1;
        static double InstalledRamMb()
        {
            if (installedRamMb < 0)
            {
                long kb;
                installedRamMb = GetPhysicallyInstalledSystemMemory(out kb) ? kb / 1024.0 : double.NaN;
            }
            return installedRamMb;
        }

        static string Dump(List<Reading> rs)
        {
            string[] types = { "none", "temp", "volt", "fan", "current", "power", "clock", "usage", "other" };
            StringBuilder sb = new StringBuilder();
            foreach (Reading r in rs)
            {
                string t = r.Type < types.Length ? types[r.Type] : r.Type.ToString();
                sb.Append(r.Sensor).Append(" | ").Append(r.Label).Append(" [").Append(r.LabelOrig).Append("] | ").Append(t).Append(" | ")
                  .Append(r.Value.ToString("0.##", CultureInfo.InvariantCulture)).Append(' ').Append(r.Unit).Append('\n');
            }
            return sb.ToString();
        }

        // ---------------------------------------------------------------- iRacing

        static void RaceLoop()
        {
            IRacing ir = new IRacing();
            FuelTracker fuel = new FuelTracker();
            while (true)
            {
                bool ok = false;
                try { ok = ir.Update(); }
                catch (Exception) { ir.Close(); }
                if (!ok)
                {
                    raceJson = "{\"connected\":false}";
                    fuel.Reset();
                    Thread.Sleep(500);
                    continue;
                }
                try { raceJson = BuildRace(ir, fuel); }
                catch (Exception ex) { raceJson = new Json().Bool("connected", false).Str("err", ex.Message).ToString(); }
                Thread.Sleep(16);
            }
        }

        static string BuildRace(IRacing ir, FuelTracker fuel)
        {
            double sl1 = Pick(ir.Get("PlayerCarSLFirstRPM"), ir.YamlNum("DriverCarSLFirstRPM"));
            double slShift = Pick(ir.Get("PlayerCarSLShiftRPM"), ir.YamlNum("DriverCarSLShiftRPM"));
            double slLast = Pick(ir.Get("PlayerCarSLLastRPM"), ir.YamlNum("DriverCarSLLastRPM"));
            double slBlink = Pick(ir.Get("PlayerCarSLBlinkRPM"), ir.YamlNum("DriverCarSLBlinkRPM"));

            double fuelL = ir.Get("FuelLevel");
            bool onPit = ir.Get("OnPitRoad") > 0;
            fuel.Update((int)ir.Get("LapCompleted"), fuelL, onPit);
            double perLap = fuel.PerLap;

            Json j = new Json().Bool("connected", true);
            j.Bool("onTrack", ir.Get("IsOnTrack") > 0);
            j.Num("speed", ir.Get("Speed") * 3.6, 0);
            j.Num("rpm", ir.Get("RPM"), 0);
            j.Num("gear", ir.Get("Gear"), 0);
            j.Num("thr", ir.Get("Throttle"), 3);
            j.Num("brk", ir.Get("Brake"), 3);
            j.Num("sl1", sl1, 0).Num("slShift", slShift, 0).Num("slLast", slLast, 0).Num("slBlink", slBlink, 0);
            j.Num("redline", ir.YamlNum("DriverCarRedLine"), 0);
            j.Num("fuel", fuelL, 2);
            j.Num("fuelMax", ir.YamlNum("DriverCarFuelMaxLtr"), 1);
            j.Num("fuelPerLap", perLap, 3);
            j.Num("fuelLaps", perLap > 0 ? fuelL / perLap : double.NaN, 1);
            j.Num("lap", ir.Get("Lap"), 0);
            j.Num("lapsLeft", ir.Get("SessionLapsRemainEx"), 0);
            j.Num("timeLeft", ir.Get("SessionTimeRemain"), 0);
            j.Num("pos", ir.Get("PlayerCarPosition"), 0);
            j.Num("classPos", ir.Get("PlayerCarClassPosition"), 0);
            j.Num("cur", ir.Get("LapCurrentLapTime"), 3);
            j.Num("last", ir.Get("LapLastLapTime"), 3);
            j.Num("best", ir.Get("LapBestLapTime"), 3);
            j.Num("delta", ir.Get("LapDeltaToBestLap"), 3);
            j.Bool("deltaOk", ir.Get("LapDeltaToBestLap_OK") > 0);
            j.Num("flags", ir.Get("SessionFlags"), 0);
            j.Num("warn", ir.Get("EngineWarnings"), 0);
            j.Bool("pit", onPit);
            j.Num("water", ir.Get("WaterTemp"), 0);
            j.Num("oil", ir.Get("OilTemp"), 0);
            j.Num("air", ir.Get("AirTemp"), 0);
            j.Num("trackTemp", ir.Get("TrackTempCrew"), 0);
            j.Num("bb", ir.Get("dcBrakeBias"), 1);
            j.Num("inc", ir.Get("PlayerCarMyIncidentCount"), 0);
            j.Num("fps", ir.Get("FrameRate"), 0);
            return j.ToString();
        }

        static double Pick(double a, double b) { return a > 0 ? a : b; }
    }

    // ==================================================================== HWiNFO shared memory

    class Reading
    {
        public int Type;
        public string Sensor, Label, LabelOrig, Unit;
        public double Value;
    }

    class HwInfo
    {
        const string MapName = "Global\\HWiNFO_SENS_SM2";
        MemoryMappedFile mmf;
        MemoryMappedViewAccessor acc;

        public void Close()
        {
            if (acc != null) acc.Dispose();
            if (mmf != null) mmf.Dispose();
            acc = null;
            mmf = null;
        }

        public List<Reading> Read()
        {
            if (acc == null)
            {
                try
                {
                    mmf = MemoryMappedFile.OpenExisting(MapName, MemoryMappedFileRights.Read);
                    acc = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                }
                catch (Exception) { Close(); return null; }
            }

            // HWiNFO kapaninca imza "DEAD" olur; eski haritayi birakip yeniden baglaniriz.
            byte[] sig = new byte[4];
            acc.ReadArray(0, sig, 0, 4);
            string sigText = Encoding.ASCII.GetString(sig);
            if (sigText == "DEAD" || sigText == "DAED") { Close(); return null; }

            long offS = acc.ReadUInt32(20); long sizeS = acc.ReadUInt32(24); long numS = acc.ReadUInt32(28);
            long offR = acc.ReadUInt32(32); long sizeR = acc.ReadUInt32(36); long numR = acc.ReadUInt32(40);
            if (numS > 2000 || numR > 20000 || sizeR < 316 || sizeS < 264) { Close(); return null; }

            string[] sensors = new string[numS];
            for (long i = 0; i < numS; i++)
            {
                long b = offS + i * sizeS;
                string user = Str(b + 136, 128);
                sensors[i] = user.Length > 0 ? user : Str(b + 8, 128);
            }

            List<Reading> list = new List<Reading>((int)numR);
            for (long i = 0; i < numR; i++)
            {
                long b = offR + i * sizeR;
                Reading r = new Reading();
                r.Type = (int)acc.ReadUInt32(b);
                uint si = acc.ReadUInt32(b + 4);
                r.Sensor = si < numS ? sensors[si] : "";
                r.LabelOrig = Str(b + 12, 128);
                string user = Str(b + 140, 128);
                r.Label = user.Length > 0 ? user : r.LabelOrig;
                r.Unit = Str(b + 268, 16);
                r.Value = acc.ReadDouble(b + 284);
                list.Add(r);
            }
            return list;
        }

        string Str(long pos, int max)
        {
            byte[] buf = new byte[max];
            acc.ReadArray(pos, buf, 0, max);
            int n = Array.IndexOf(buf, (byte)0);
            if (n < 0) n = max;
            return Encoding.Default.GetString(buf, 0, n).Trim();
        }
    }

    // ==================================================================== iRacing SDK shared memory

    class IRacing
    {
        class VarHeader { public int Type, Offset; }

        MemoryMappedFile mmf;
        MemoryMappedViewAccessor acc;
        readonly Dictionary<string, VarHeader> vars = new Dictionary<string, VarHeader>();
        int varsNum = -1, varsOffset = -1, sessionVer = -1;
        string sessionYaml = "";
        readonly Dictionary<string, double> yamlCache = new Dictionary<string, double>();
        byte[] buf = new byte[0];

        public void Close()
        {
            if (acc != null) acc.Dispose();
            if (mmf != null) mmf.Dispose();
            acc = null;
            mmf = null;
            varsNum = varsOffset = sessionVer = -1;
            vars.Clear();
            yamlCache.Clear();
        }

        public bool Update()
        {
            if (acc == null)
            {
                try
                {
                    mmf = MemoryMappedFile.OpenExisting("Local\\IRSDKMemMapFileName", MemoryMappedFileRights.Read);
                    acc = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                }
                catch (Exception) { Close(); return false; }
            }

            // iRacing kapaninca harita bizim tuttugumuz kopya olarak kalabilir; birakip yeniden aciyoruz.
            if ((acc.ReadInt32(4) & 1) == 0) { Close(); return false; }

            int numVars = acc.ReadInt32(24), varOff = acc.ReadInt32(28);
            if (numVars != varsNum || varOff != varsOffset) LoadVars(numVars, varOff);

            int sv = acc.ReadInt32(12);
            if (sv != sessionVer)
            {
                int len = acc.ReadInt32(16), off = acc.ReadInt32(20);
                byte[] s = new byte[len];
                acc.ReadArray(off, s, 0, len);
                int n = Array.IndexOf(s, (byte)0);
                sessionYaml = Encoding.GetEncoding(1252).GetString(s, 0, n < 0 ? len : n);
                yamlCache.Clear();
                sessionVer = sv;
            }

            int numBuf = Math.Min(acc.ReadInt32(32), 4), bufLen = acc.ReadInt32(36);
            if (buf.Length != bufLen) buf = new byte[bufLen];
            for (int attempt = 0; attempt < 3; attempt++)
            {
                int best = 0, bestTick = int.MinValue;
                for (int i = 0; i < numBuf; i++)
                {
                    int tick = acc.ReadInt32(48 + i * 16);
                    if (tick > bestTick) { bestTick = tick; best = i; }
                }
                acc.ReadArray(acc.ReadInt32(48 + best * 16 + 4), buf, 0, bufLen);
                if (acc.ReadInt32(48 + best * 16) == bestTick) return true;
            }
            return true;
        }

        void LoadVars(int numVars, int varOff)
        {
            vars.Clear();
            byte[] name = new byte[32];
            for (int i = 0; i < numVars; i++)
            {
                long b = varOff + i * 144L;
                VarHeader v = new VarHeader();
                v.Type = acc.ReadInt32(b);
                v.Offset = acc.ReadInt32(b + 4);
                acc.ReadArray(b + 16, name, 0, 32);
                int n = Array.IndexOf(name, (byte)0);
                vars[Encoding.ASCII.GetString(name, 0, n < 0 ? 32 : n)] = v;
            }
            varsNum = numVars;
            varsOffset = varOff;
        }

        public double Get(string name)
        {
            VarHeader v;
            if (!vars.TryGetValue(name, out v) || v.Offset < 0 || v.Offset + 8 > buf.Length) return double.NaN;
            switch (v.Type)
            {
                case 0: return buf[v.Offset];
                case 1: return buf[v.Offset] != 0 ? 1 : 0;
                case 2:
                case 3: return BitConverter.ToInt32(buf, v.Offset);
                case 4: return BitConverter.ToSingle(buf, v.Offset);
                case 5: return BitConverter.ToDouble(buf, v.Offset);
                default: return double.NaN;
            }
        }

        public double YamlNum(string key)
        {
            double d;
            if (yamlCache.TryGetValue(key, out d)) return d;
            d = double.NaN;
            Match m = Regex.Match(sessionYaml, "\\b" + key + ":\\s*(-?[0-9.]+)");
            if (m.Success) double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out d);
            yamlCache[key] = d;
            return d;
        }
    }

    // Son 5 temiz turun ortalama yakit tuketimi (pit / yakit alma / yarim tur sayilmaz).
    class FuelTracker
    {
        int lastLap = -1;
        double lapStartFuel;
        bool dirty;
        readonly List<double> uses = new List<double>();

        public void Reset() { lastLap = -1; uses.Clear(); }

        public void Update(int lapCompleted, double fuel, bool pit)
        {
            if (double.IsNaN(fuel)) return;
            if (pit) dirty = true;
            if (lastLap < 0) { lastLap = lapCompleted; lapStartFuel = fuel; dirty = true; return; }
            if (fuel > lapStartFuel + 0.3) { lapStartFuel = fuel; dirty = true; }
            if (lapCompleted == lastLap) return;
            if (lapCompleted == lastLap + 1 && !dirty)
            {
                double used = lapStartFuel - fuel;
                if (used > 0.05)
                {
                    uses.Add(used);
                    if (uses.Count > 5) uses.RemoveAt(0);
                }
            }
            lastLap = lapCompleted;
            lapStartFuel = fuel;
            dirty = pit;
        }

        public double PerLap
        {
            get
            {
                if (uses.Count == 0) return double.NaN;
                double s = 0;
                foreach (double u in uses) s += u;
                return s / uses.Count;
            }
        }
    }

    // ==================================================================== Mini JSON yazici

    class Json
    {
        readonly StringBuilder sb = new StringBuilder("{");
        bool first = true;

        void Key(string k)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append('"').Append(k).Append("\":");
        }

        public Json Num(string k, double v, int dec)
        {
            Key(k);
            if (double.IsNaN(v) || double.IsInfinity(v)) sb.Append("null");
            else sb.Append(Math.Round(v, dec).ToString(CultureInfo.InvariantCulture));
            return this;
        }

        public Json Bool(string k, bool v) { Key(k); sb.Append(v ? "true" : "false"); return this; }

        public Json Raw(string k, string raw) { Key(k); sb.Append(raw); return this; }

        public Json Str(string k, string v)
        {
            Key(k);
            sb.Append('"');
            foreach (char c in v ?? "")
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                else sb.Append(c);
            }
            sb.Append('"');
            return this;
        }

        public override string ToString() { return sb.ToString() + "}"; }
    }
}
