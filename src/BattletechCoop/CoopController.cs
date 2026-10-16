using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using BattleTech;
using BattletechCoop.Net;
using Steamworks;
using UnityEngine;

namespace BattletechCoop
{
    // Owns the co-op session for the lifetime of the game process: starts it from the
    // control window, from launch arguments or from a Steam invite, pumps it every frame,
    // and shows a small status overlay. Ctrl+Shift+C toggles the window.
    //
    // Launch arguments (for development and automated tests):
    //   -coop-host-tcp <port>       host over TCP (loopback only unless -coop-lan)
    //   -coop-join-tcp <host:port>  join over TCP
    //   -coop-lan                   bind the TCP host to all interfaces
    //   -coop-name <name>           player name (default: Steam persona name)
    //   -coop-test-message          once connected, send a test game message to the other side
    //   +connect_lobby <id>         join a Steam lobby (Steam passes this when launching from an invite)
    public class CoopController : MonoBehaviour
    {
        public const ushort TestChannel = 1;
        private const int DefaultTcpPort = 47637;

        public static CoopController Instance { get; private set; }
        public CoopSession Session { get; private set; }

        private string modsDirectory;
        private string manifest;
        private Callback<GameLobbyJoinRequested_t> lobbyJoinRequested;
        private Callback<GameRichPresenceJoinRequested_t> richPresenceJoinRequested;
        private bool steamReady;
        private ulong pendingLobby;
        private string pendingName;
        private string lobbyField = "";
        private bool showWindow;
        private Rect windowRect = new Rect(40, 40, 420, 360);
        private string joinAddress = "127.0.0.1:" + DefaultTcpPort;
        private string hostPort = DefaultTcpPort.ToString();
        private bool sendTestOnConnect;
        private bool testSent;
        private string lastEvent = "";

        public static void Create(string modDirectory)
        {
            var go = new GameObject("BattletechCoop");
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<CoopController>();
            Instance.modsDirectory = Path.GetDirectoryName(modDirectory.TrimEnd('\\', '/'));
        }

        private void Start()
        {
            manifest = ModManifest.Compute(modsDirectory);
            Log.Info($"Mod manifest: {manifest}");
            HandleLaunchArguments();
        }

        // ModTek starts mods before the game initializes Steam, so Steam callbacks are registered
        // on the first frame where SteamManager reports ready, not in Start().
        private void EnsureSteam()
        {
            if (steamReady || !SteamManager.Initialized) return;
            steamReady = true;
            lobbyJoinRequested = Callback<GameLobbyJoinRequested_t>.Create(OnLobbyJoinRequested);
            richPresenceJoinRequested = Callback<GameRichPresenceJoinRequested_t>.Create(OnRichPresenceJoinRequested);
            Log.Info($"Steam ready as '{SteamFriends.GetPersonaName()}'; listening for invites");
            if (pendingLobby != 0)
            {
                var lobby = pendingLobby;
                pendingLobby = 0;
                JoinSteam(lobby, pendingName);
            }
        }

        private void HandleLaunchArguments()
        {
            var args = Environment.GetCommandLineArgs();
            string name = null;
            for (var i = 0; i < args.Length; i++)
            {
                var hasValue = i + 1 < args.Length;
                switch (args[i])
                {
                    case "-coop-name": if (hasValue) name = args[i + 1]; break;
                    case "-coop-test-message": sendTestOnConnect = true; break;
                }
            }
            var lan = Array.IndexOf(args, "-coop-lan") >= 0;
            for (var i = 0; i + 1 < args.Length; i++)
            {
                switch (args[i])
                {
                    case "-coop-host-tcp": HostTcp(int.Parse(args[i + 1]), lan, name); return;
                    case "-coop-join-tcp": JoinTcp(args[i + 1], name); return;
                    case "+connect_lobby":
                        // Steam isn't initialized yet this early; EnsureSteam() joins once it is.
                        pendingLobby = ulong.Parse(args[i + 1]);
                        pendingName = name;
                        Log.Info($"Launched from a Steam invite for lobby {pendingLobby}; joining once Steam is ready");
                        return;
                }
            }
        }

        private string DefaultName(string requested)
        {
            if (!string.IsNullOrEmpty(requested)) return requested;
            return SteamManager.Initialized ? SteamFriends.GetPersonaName() : "Player";
        }

        private string ModVersion => typeof(CoopController).Assembly.GetName().Version.ToString();

        public void HostTcp(int port, bool lan, string name = null)
        {
            if (!CanStart()) return;
            try
            {
                var transport = TcpTransport.Listen(lan ? IPAddress.Any : IPAddress.Loopback, port);
                Begin(CoopSession.Host(transport, DefaultName(name), ModVersion, manifest, Log.Info));
            }
            catch (Exception e)
            {
                Notify("Could not host on port " + port + ": " + e.Message);
            }
        }

        public void JoinTcp(string address, string name = null)
        {
            if (!CanStart()) return;
            var colon = address.LastIndexOf(':');
            var host = colon > 0 ? address.Substring(0, colon) : address;
            var port = colon > 0 ? int.Parse(address.Substring(colon + 1)) : DefaultTcpPort;
            Begin(CoopSession.Join(TcpTransport.Connect(host, port), DefaultName(name), ModVersion, manifest, Log.Info));
        }

        public void HostSteam(string name = null)
        {
            if (!CanStart()) return;
            if (!SteamManager.Initialized) { Notify("Steam is not running"); return; }
            Begin(CoopSession.Host(SteamTransport.Host(4, Log.Info), DefaultName(name), ModVersion, manifest, Log.Info));
        }

        public void JoinSteam(ulong lobby, string name = null)
        {
            if (!CanStart()) return;
            if (!SteamManager.Initialized) { Notify("Steam is not running"); return; }
            Begin(CoopSession.Join(SteamTransport.Join(lobby, Log.Info), DefaultName(name), ModVersion, manifest, Log.Info));
        }

        public void Leave()
        {
            Session?.Close("left the session");
        }

        private bool CanStart()
        {
            if (Session != null && Session.IsOpen) { Notify("Already in a session; leave it first"); return false; }
            return true;
        }

        private void Begin(CoopSession session)
        {
            Session = session;
            testSent = false;
            session.PlayerJoined += p => Notify($"{p.Name} is player {p.Index}");
            session.PlayerLeft += (p, reason) => Notify($"{p.Name} left: {reason}");
            session.Closed += reason => Notify("Session ended: " + reason);
            session.GameMessage += OnGameMessage;
        }

        private void OnGameMessage(byte sender, ushort channel, byte[] payload)
        {
            if (channel != TestChannel) return;
            Notify($"Test message from player {sender}: {payload.Length} bytes, {Encoding.UTF8.GetString(payload, 0, Math.Min(payload.Length, 80))}");
        }

        public void SendTestMessage(int extraBytes = 0)
        {
            if (Session == null || Session.State != SessionState.Active) return;
            var text = $"hello from player {Session.LocalIndex} ({Session.LocalName})";
            var head = Encoding.UTF8.GetBytes(text);
            var payload = new byte[head.Length + extraBytes];
            Buffer.BlockCopy(head, 0, payload, 0, head.Length);
            Session.SendGame(TestChannel, payload);
            Log.Info($"Sent test message ({payload.Length} bytes)");
        }

        private void OnLobbyJoinRequested(GameLobbyJoinRequested_t request)
        {
            Log.Info($"Steam invite accepted: lobby {request.m_steamIDLobby.m_SteamID} from {request.m_steamIDFriend.m_SteamID}");
            JoinSteam(request.m_steamIDLobby.m_SteamID);
        }

        // "Join Game" on a friend in the Steam friends list while our game is running.
        private void OnRichPresenceJoinRequested(GameRichPresenceJoinRequested_t request)
        {
            Log.Info($"Steam Join Game: '{request.m_rgchConnect}' from {request.m_steamIDFriend.m_SteamID}");
            ulong lobby;
            var parts = (request.m_rgchConnect ?? "").Split(' ');
            if (parts.Length == 2 && parts[0] == "+connect_lobby" && ulong.TryParse(parts[1], out lobby))
                JoinSteam(lobby);
        }

        private void Notify(string message)
        {
            lastEvent = message;
            Log.Info(message);
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.C) && Input.GetKey(KeyCode.LeftControl) && Input.GetKey(KeyCode.LeftShift))
                showWindow = !showWindow;

            EnsureSteam();
            if (Session == null) return;
            try
            {
                Session.Update();
            }
            catch (Exception e)
            {
                Log.Error("Session update failed", e);
            }
            if (sendTestOnConnect && !testSent && Session.State == SessionState.Active
                && (Session.Role == SessionRole.Guest || Session.Players.Count() > 1))
            {
                testSent = true;
                SendTestMessage();
            }
        }

        private void OnApplicationQuit()
        {
            Session?.Close("game closed");
        }

        private void OnGUI()
        {
            if (Session != null && Session.IsOpen)
            {
                var label = $"Co-op {Session.Role} · {Session.State} · {Session.Players.Count()} player(s)";
                GUI.Label(new Rect(Screen.width - 330, 8, 320, 22), label);
            }
            if (showWindow)
                windowRect = GUILayout.Window(0x0B7C00, windowRect, DrawWindow, "BattleTech Co-op (Ctrl+Shift+C)");
        }

        private void DrawWindow(int id)
        {
            if (Session == null || !Session.IsOpen)
            {
                GUILayout.Label("Not connected.");
                GUILayout.Label(steamReady ? "Steam ready" : "Waiting for Steam...");
                if (GUILayout.Button("Host via Steam")) HostSteam();
                GUILayout.BeginHorizontal();
                GUILayout.Label("Lobby ID", GUILayout.Width(60));
                lobbyField = GUILayout.TextField(lobbyField, GUILayout.Width(170));
                ulong lobbyId;
                if (GUILayout.Button("Join Steam lobby") && ulong.TryParse(lobbyField.Trim(), out lobbyId)) JoinSteam(lobbyId);
                GUILayout.EndHorizontal();
                GUILayout.Space(8);
                GUILayout.Label("Development (TCP):");
                GUILayout.BeginHorizontal();
                GUILayout.Label("Port", GUILayout.Width(40));
                hostPort = GUILayout.TextField(hostPort, GUILayout.Width(70));
                if (GUILayout.Button("Host TCP")) HostTcp(int.Parse(hostPort), false);
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                joinAddress = GUILayout.TextField(joinAddress, GUILayout.Width(200));
                if (GUILayout.Button("Join TCP")) JoinTcp(joinAddress);
                GUILayout.EndHorizontal();
            }
            else
            {
                GUILayout.Label($"{Session.Role} via {Session.Transport.Name}");
                GUILayout.Label($"State: {Session.State}   You are player {Session.LocalIndex}");
                foreach (var p in Session.Players)
                    GUILayout.Label($"  [{p.Index}] {p.Name}   {(p.RttMs < 0 ? "-" : p.RttMs.ToString("0") + " ms")}");
                var steam = Session.Transport as SteamTransport;
                if (steam != null && steam.LobbyReady)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label($"Lobby {steam.Lobby.m_SteamID}");
                    if (GUILayout.Button("Copy ID", GUILayout.Width(70))) GUIUtility.systemCopyBuffer = steam.Lobby.m_SteamID.ToString();
                    GUILayout.EndHorizontal();
                    if (Session.Role == SessionRole.Host && GUILayout.Button("Invite Steam friends")) steam.InviteFriends();
                }
                if (Session.State != SessionState.Active) GUILayout.Label("Connecting to host...");
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Send test message")) SendTestMessage();
                if (GUILayout.Button("Send 2 MB test")) SendTestMessage(2 * 1024 * 1024);
                GUILayout.EndHorizontal();
                if (GUILayout.Button("Leave session")) Leave();
            }
            GUILayout.Space(8);
            GUILayout.Label(lastEvent);
            GUILayout.Label($"Mods: {manifest}");
            GUI.DragWindow();
        }
    }
}
