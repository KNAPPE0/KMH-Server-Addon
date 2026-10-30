// The ONLY place RWT's renames live, so feature code never names a generation itself.
#if RWT_NEW
global using RTNetwork.Components;
global using RTNetwork.Packets;
global using RTShared;
global using RTShared.Misc;
global using RTShared.Commands;
global using TCPNetwork = RTNetwork.Components;
global using Shared = RTShared;
#else
global using TCPNetwork;
global using TCPNetwork.Packets;
global using TCPNetwork.Files.Client;
global using Shared;
global using Shared.Misc;
global using Shared.Commands;
#endif

#if RWT_RT
global using RTServer.PacketManagers;
global using RTServer.Core;
global using RTServer.Misc;
#else
global using GameServer.PacketManager;
global using GameServer.Core;
global using GameServer.Misc;
#endif

#if RWT_RT
global using UserFile = RTShared.Files.Player.FL_Player;
#elif RWT_NEW
global using UserFile = RTShared.Files.ServerClient.FL_Player;
#endif

namespace KMHServerAddon
{
    internal static class RwtCompat
    {
#if RWT_NEW
        private const string       ChatHeaderName     = "Chat";
        private const PacketHeader ChatHeaderFallback = PacketHeader.Chat;
#else
        private const string       ChatHeaderName     = "ChatManager";
        private const PacketHeader ChatHeaderFallback = PacketHeader.ChatManager;
#endif
        private static PacketHeader? _chatHeader;

        private static bool _versionResolved;
        private static System.Reflection.PropertyInfo _versionProp;
        private static System.Reflection.FieldInfo _versionField;

        // Reflected: RWT 26.8.31.1 made this a field, and a direct call binds at JIT, throwing before the caller's try.
        public static string ExecutableVersion
        {
            get
            {
                if (!_versionResolved)
                {
                    _versionResolved = true;
                    try
                    {
                        System.Type t = typeof(CommonValues);
                        _versionProp  = t.GetProperty("ExecutableVersion", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                        _versionField = t.GetField("ExecutableVersion", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    }
                    catch { }
                }
                try
                {
                    if (_versionProp  != null) return _versionProp.GetValue(null)  as string ?? "";
                    if (_versionField != null) return _versionField.GetValue(null) as string ?? "";
                }
                catch { }
                return "";
            }
        }

#if RWT_RT
        private const string UserStoreName = "RTServer.Managers.UserManagerH";
#else
        private const string UserStoreName = "GameServer.Managers.UserManagerH";
#endif

        // Reflected: RWT reshapes its account store within a generation, and a bound call throws before the caller's try.
        internal static System.Reflection.MethodInfo UserStoreMethod(string name, int parameters)
        {
            System.Type store = typeof(PM_Login).Assembly.GetType(UserStoreName, throwOnError: false);
            if (store == null) return null;
            foreach (System.Reflection.MethodInfo m in store.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
                if (m.Name == name && m.GetParameters().Length == parameters) return m;
            return null;
        }

        // Reflected: 26.10.4.1 made Listener.TargetClient private and the compiled call dropped every handshake.
        private static System.Func<Listener, ServerClient> _targetClient;
        internal static bool TargetClientResolved => (_targetClient ??= InstanceGetter<Listener, ServerClient>("TargetClient")) != null;
        public static ServerClient TargetClientOf(Listener listener)
            => listener == null || !TargetClientResolved ? null : _targetClient(listener);

        // A property or field of any access, declared on TOwner or a base; null when there is none of a usable type.
        internal static System.Func<TOwner, TValue> InstanceGetter<TOwner, TValue>(string name)
        {
            const System.Reflection.BindingFlags any = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly;
            for (System.Type t = typeof(TOwner); t != null; t = t.BaseType)
            {
                System.Reflection.MethodInfo get = t.GetProperty(name, any)?.GetGetMethod(true);
                if (get != null && get.GetParameters().Length == 0 && typeof(TValue).IsAssignableFrom(get.ReturnType))
                {
                    try { return (System.Func<TOwner, TValue>)System.Delegate.CreateDelegate(typeof(System.Func<TOwner, TValue>), get); }
                    catch (System.ArgumentException) { return o => (TValue)get.Invoke(o, null); }
                }
                System.Reflection.FieldInfo field = t.GetField(name, any);
                if (field != null && typeof(TValue).IsAssignableFrom(field.FieldType)) return o => (TValue)field.GetValue(o);
            }
            return null;
        }

        // By name at runtime: RWT renumbers PacketHeader between builds, so a baked ordinal mis-tags KMH chat.
        public static PacketHeader ChatHeader
        {
            get
            {
                if (_chatHeader.HasValue) return _chatHeader.Value;
                try { _chatHeader = (PacketHeader)System.Enum.Parse(typeof(PacketHeader), ChatHeaderName); }
                catch { _chatHeader = ChatHeaderFallback; }
                return _chatHeader.Value;
            }
        }
    }
}
