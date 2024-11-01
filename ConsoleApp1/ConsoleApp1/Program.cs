using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using MyTrueGear;
using System.Diagnostics;
using Microsoft.Win32;

using System;
using System.Runtime.InteropServices;

class Program
{
    private static HttpListener listener;
    //private static bool isFirst = true;
    private static Dictionary<string, string> keyNamePairs = new Dictionary<string, string>();
    private static readonly HttpClient client = new HttpClient();
    private static int previousLength = 0;
    private static TrueGearMod _TrueGear = null;

    private static string lastEvent = null;

    private static string _SteamExe;
    public const string STEAM_OPENURL = "steam://rungameid/658920";


    private const int SW_MINIMIZE = 6;
    private const int SW_RESTORE = 9;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    public static string SteamExePath()
    {
        return (string)Registry.GetValue(@"HKEY_CURRENT_USER\SOFTWARE\Valve\Steam", "SteamExe", null);
    }

    static async Task Main()
    {
        //当有两个程序运行的时候，关闭前一个程序，保留当前程序
        string currentProcessName = Process.GetCurrentProcess().ProcessName;
        Process[] processes = Process.GetProcessesByName(currentProcessName);
        if (processes.Length > 1)
        {
            if (processes[0].UserProcessorTime.TotalMilliseconds > processes[1].UserProcessorTime.TotalMilliseconds)
            {
                processes[0].Kill();
            }
            else
            {
                processes[1].Kill();
            }
        }

        // 获取当前 Console 窗口句柄
        IntPtr hWnd = GetConsoleWindow();

        if (hWnd != IntPtr.Zero)
        {
            // 最小化窗口
            ShowWindow(hWnd, SW_MINIMIZE);
        }

        string appName = "BhapticsPlayer";

        foreach (var process in Process.GetProcessesByName(appName))
        {
            try
            {
                process.Kill(); // 尝试结束进程
                process.WaitForExit(); // 等待进程退出
            }
            catch (Exception ex)
            {
                //Console.WriteLine($"无法关闭进程 {appName}. 错误: {ex.Message}");
            }
        }

        Thread.Sleep(500);
        _SteamExe = SteamExePath();
        if (_SteamExe != null) Process.Start(_SteamExe, STEAM_OPENURL);

        Thread.Sleep(500);

        listener = new HttpListener();
        listener.Prefixes.Add("http://127.0.0.1:15881/v2/feedbacks/");
        listener.Start();

        //Console.WriteLine("WebSocket server started at ws://localhost:15881/v2/feedbacks/");
        //Console.WriteLine("Press Enter to exit...");

        _TrueGear = new TrueGearMod();

        _TrueGear.LOG("Start");

        await StartListening();

        Console.ReadLine();
        listener.Close();
    }

    private static async Task StartListening()
    {
        while (true)
        {
            var context = await listener.GetContextAsync();
            if (context.Request.Url.AbsolutePath == "/v2/feedbacks" && context.Request.IsWebSocketRequest)
            {
                ProcessWebSocketRequest(context);
            }
            else
            {
                context.Response.StatusCode = 400;
                context.Response.Close();
            }
        }
    }

    private static async void ProcessWebSocketRequest(HttpListenerContext context)
    {
        try
        {
            var webSocketContext = await context.AcceptWebSocketAsync(subProtocol: null);
            var webSocket = webSocketContext.WebSocket;

            byte[] buffer = new byte[1024];
            var messageBuffer = new List<byte>();

            new Thread(new ThreadStart(Program.ClearLastEvent)).Start();

            while (webSocket.State == WebSocketState.Open)
            {
                var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None);
                }
                else
                {
                    // 当您有新的数据添加到messageBuffer时
                    messageBuffer.AddRange(new ArraySegment<byte>(buffer, 0, result.Count));

                    

                    // Check if this is the final segment of the message
                    if (result.EndOfMessage)
                    {
                        // 仅获取新添加的数据
                        var newData = messageBuffer.Skip(previousLength).ToArray();
                        var newMessage = Encoding.UTF8.GetString(newData);
                        previousLength = messageBuffer.Count;
                        //Console.WriteLine(newMessage);
                        await FixJson(newMessage);
                    }
                }

                //await Task.Delay(20);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Exception: {ex.Message}");
        }
    }

    public static async Task FixJson(string inputJson)
    {
        try
        {
            var matches = Regex.Matches(inputJson, @"(\{(?:[^{}]|(?<o>\{)|(?<-o>\}))+(?(o)(?!))\})");
            var jArray = new JArray();
            foreach (Match match in matches)
            {
                var parsedObject = JObject.Parse(match.Value);
                ProcessJson(parsedObject);
                await ProcessJson(parsedObject);
                jArray.Add(parsedObject);

            }

            //return jArray.ToString();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
           // return string.Empty;
        }
    }

    public static async Task ProcessJson(JObject jsonObject)
    {
        var registerEntries = jsonObject["Register"]?.Children();
        var submitEntries = jsonObject["Submit"]?.Children();

        if (submitEntries != null)
        {
            foreach (var entry in submitEntries)
            {
                string key = entry["Key"]?.ToString();
                string name = entry["Project"]?["name"]?.ToString();
                if (key != null && key != "" && key != "ShakeVest")
                {
                    Console.WriteLine("-----------------------------------------------");
                    var triggerEvent = EventCheck(key);
                    if (key.Contains("Hit"))
                    {
                        float offsetAngleX = (float)(entry["Parameters"]?["rotationOption"]?["offsetAngleX"]);
                        float offsetY = (float)(entry["Parameters"]?["rotationOption"]?["offsetY"]);
                        if (offsetAngleX < 0)
                        {
                            offsetAngleX = 360 + offsetAngleX;
                        }
                        Console.WriteLine($"Old_Name: {key}|Angle :{offsetAngleX}|Y :{offsetY}\n");
                        Console.WriteLine($"New_Name: {triggerEvent}|Angle :{offsetAngleX}|Y :{offsetY}\n");

                        if (lastEvent == $"{triggerEvent}|Angle :{offsetAngleX}|Y :{offsetY}")
                        {
                            return;
                        }

                        _TrueGear.PlayAngle(triggerEvent, offsetAngleX, offsetY);
                        lastEvent = $"{triggerEvent}|Angle :{offsetAngleX}|Y :{offsetY}";
                        //_TrueGear.LOG("------------------------------------------------");
                        //_TrueGear.LOG($"Old_Name: {key}|Angle :{offsetAngleX}|Y :{offsetY}");
                        //_TrueGear.LOG($"New_Name: {triggerEvent}|Angle :{offsetAngleX}|Y :{offsetY}");
                    }
                    else
                    {
                        Console.Write($"Old_Name: {key}\n");
                        Console.Write($"New_Name: {triggerEvent}\n");

                        if (lastEvent == triggerEvent)
                        {
                            return;
                        }

                        _TrueGear.Play(triggerEvent);
                        lastEvent = triggerEvent;

                        //_TrueGear.LOG("------------------------------------------------");
                        //_TrueGear.LOG($"Old_Name: {key}");
                        //_TrueGear.LOG($"New_Name: {triggerEvent}");
                    }
                    
                    //
                    await Task.Delay(20); // 暂停20毫秒
                }
            }
        }
    }

    public static void ClearLastEvent()
    {
        while (true)
        {
            lastEvent = "";
            //Console.WriteLine("clear lastevent");
            Thread.Sleep(50);
        }
    }

    public static string EventCheck(string e)
    {
        string x = null;
        string side = null;
        string weapon = null;
        string damage = null;

        if (e.Contains("RVest") || e.Contains("RArms"))
        {
            side = "RightHand";
        }
        else if (e.Contains("LVest") || e.Contains("LArms"))
        {
            side = "LeftHand";
        }

        if (e.Contains("Shoot"))
        {
            if (e.Contains("Pistol"))
            {
                weapon = "PistolShoot";
            }
            else if (e.Contains("Revolver"))
            {
                weapon = "PistolShoot";
            }
            else if (e.Contains("Crossbow"))
            {
                weapon = "PistolShoot";
            }
            else if (e.Contains("SMG"))
            {
                weapon = "RifleShoot";
            }
            else if (e.Contains("AR2"))
            {
                weapon = "RifleShoot";
            }
            else if (e.Contains("Shotgun"))
            {
                weapon = "ShotgunShoot";
            }
            else if (e.Contains("RPG"))
            {
                weapon = "ShotgunShoot";
            }
            else if (e.Contains("GravGun"))
            {
                weapon = "ShotgunShoot";
            }
            else if (e.Contains("StationaryGun"))
            {
                weapon = "ShotgunShoot";
            }
            else
            {
                weapon = "PistolShoot";
            }
        }
        else if (e.Contains("Swing"))
        {
            weapon = "MeleeHit";
        }
        else if (e.Contains("Hit"))
        {
            if (e.Contains("Bullet"))
            {
                damage = "PlayerBulletDamage";
            }
            else if (e.Contains("Sniper"))
            {
                damage = "PlayerBulletDamage";
            }
            else if (e.Contains("Buckshot"))
            {
                damage = "DefaultDamage";
            }
            else if (e.Contains("Melee"))
            {
                damage = "DefaultDamage";
            }
            else if (e.Contains("Explosion"))
            {
                damage = "DefaultDamage";
            }
            else
            {
                damage = "DefaultDamage";
            }
        }
        else
        {
            if (e.Contains("Landing"))
            {
                damage = "FallDamage";
            }
            else if (e.Contains("Burning"))
            {
                damage = "FireDamage";
            }
            else if (e.Contains("Drowning"))
            {
                damage = "FallDamage";
            }
            else if (e.Contains("ConsumeHealth"))
            {
                damage = "Healing";
            }
            else if (e.Contains("ConsumeBattery"))
            {
                damage = "Healing";
            }
            else if (e.Contains("VehicleCollision"))
            {
                damage = "DefaultDamage";
            }
            else
            {
                damage = "DefaultDamage";
            }
        }

        if (weapon != null)
        {
            x = side + weapon;
        }
        else if (damage != null)
        {
            x = damage;
        }

        return x;
    }



}
