using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;


namespace RoslynPad.UI
{
    public enum KEY_BIND//has to be top level to be bound to
    {
        RenameSymbol,
        CommentSelection,
        UncommentSelection,
        FormatDocument,
        SaveDocument,
        TerminateRunningScript,
        RunScript,
        ResultsPanel_CopyValue,
        ResultsPanel_CopyValueWithChildren,
        NewCSDocument,
        NewCSXScript,
        OpenFile,
        CloseCurrentFile,
        ToggleDebugMode,
        Search_ReplaceNext,
        Search_ReplaceAll
    }
    public static class KeybindHelper
    {
        static KeybindHelper()
        {
            
            keyBinds = new Dictionary<KEY_BIND, KnownKeyBind>();
            AddKeyBind(KEY_BIND.RenameSymbol, "Rename Current Symbol", "F2");
            AddKeyBind(KEY_BIND.CommentSelection, "Comment Selection", "Control+K");
            AddKeyBind(KEY_BIND.UncommentSelection, "Un-Comment Selection", "Control+U");
            AddKeyBind(KEY_BIND.FormatDocument, "Format Document", "Control+D");
            AddKeyBind(KEY_BIND.SaveDocument, "Save Document", "Control+S");
            AddKeyBind(KEY_BIND.TerminateRunningScript, "Terminate Running Script", "Shift+F5");
            AddKeyBind(KEY_BIND.RunScript, "Run Script", "F5");
            AddKeyBind(KEY_BIND.ResultsPanel_CopyValue, "Results Panel -> Copy Value", "Control+C");
            AddKeyBind(KEY_BIND.ResultsPanel_CopyValueWithChildren, "Results Panel -> Copy Value with Children", "Control+Shift+C");
            AddKeyBind(KEY_BIND.NewCSDocument, "New CS Document", "Control+N");
            AddKeyBind(KEY_BIND.NewCSXScript, "New CSX Script", "Control+Shift+N");
            AddKeyBind(KEY_BIND.OpenFile, "Open File", "Control+O");
            AddKeyBind(KEY_BIND.CloseCurrentFile, "Close Currently Opened File", "Control+W");
            AddKeyBind(KEY_BIND.ToggleDebugMode, "Toggle Debug/Optimization Mode", "Control+Shift+O");
            AddKeyBind(KEY_BIND.Search_ReplaceNext, "Search -> Replace Next", "Alt+R");
            AddKeyBind(KEY_BIND.Search_ReplaceAll, "Search -> Replace All", "Alt+A");
        }
        

        public class KnownKeyBind
        {
            public KnownKeyBind(KEY_BIND name, string description, string defaultKey)
            {
                this.name = name;
                this.description = description;
                this.defaultKey = defaultKey;
                this.currentKey = defaultKey;
            }

            public KEY_BIND name { get; }
            public string description { get; }
            public string defaultKey { get; }
            public string currentKey { get; set; }
        }
        public static void SetKeyBindSequenceStr(KEY_BIND name, string sequence)
        {
            GetBindInfo(name).currentKey = sequence;
        }
        private static void AddKeyBind(KEY_BIND name, string description, string defaultKey)
        {
            keyBinds[name] = new KnownKeyBind(name, description, defaultKey);
        }
        private static Dictionary<KEY_BIND,KnownKeyBind> keyBinds;

        public static string GetDescription(KEY_BIND name, bool withCurrentBind=false)
        {
            var info = GetBindInfo(name);
            return $"{info.description}{(withCurrentBind ? $" ({info.currentKey})" : "")}";
        }
        public static string GetSequenceStr(KEY_BIND name)
        {
            return GetBindInfo(name).currentKey;
        }
        private static KnownKeyBind GetBindInfo(KEY_BIND name) => keyBinds[name];
        public static void ReadOverrides(IApplicationSettingsValues settings)
        {
            var keyOverrides = settings.KeyBindOverrides;
            if (keyOverrides == null)
                keyOverrides = new Dictionary<string, string>();
            foreach (var val in Enum.GetValues<KEY_BIND>())
            {
                var info = GetBindInfo(val);
                if (keyOverrides.TryGetValue(val.ToString(), out var keyStr) && string.IsNullOrWhiteSpace(keyStr) == false)
                    info.currentKey = keyStr;
                else
                    info.currentKey = info.defaultKey;
            }
        }
        /*
         *         converter = new KeyGestureConverter();
        private static KeyGestureConverter converter;*/

        //public static KeyGesture? GetGesture(KEY_BIND name)
        //{
        //    try
        //    {
        //        return (KeyGesture?) converter.ConvertFromString(GetSequenceStr(name));
        //    }
        //    catch {
        //        return null;
        //    }
            
        //}
    }
}
