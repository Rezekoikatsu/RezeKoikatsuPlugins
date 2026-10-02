using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using Studio;
using KKAPI.Studio.SaveLoad;
using KKAPI.Utilities;
using ExtensibleSaveFormat;

namespace StudioCharTools
{
    /// <summary>存進場景卡。KKAPI 會在存讀場景時自動呼叫。</summary>
    public class BlendShapeLockSceneController : SceneCustomFunctionController
    {
        const string KEY = "locks";

        protected override void OnSceneSave()
        {
            var sb = new StringBuilder();

            foreach (var kv in Studio.Studio.Instance.dicObjectCtrl)
            {
                var ociChar = kv.Value as OCIChar;
                if (ociChar == null) continue;

                var s = BlendShapeLock.Serialize(ociChar.charInfo);
                if (string.IsNullOrEmpty(s)) continue;

                // 每行前面補上這隻角色的 dicKey
                foreach (var line in s.Split('\n'))
                {
                    if (line.Length == 0) continue;
                    sb.Append(kv.Key).Append('\t').Append(line).Append('\n');
                }
            }

            if (sb.Length == 0) { SetExtendedData(null); return; }

            var data = new PluginData();
            data.data[KEY] = sb.ToString();     // 純字串，ExtensibleSaveFormat 自己會處理序列化
            SetExtendedData(data);
        }

        protected override void OnSceneLoad(SceneOperationKind operation,
            ReadOnlyDictionary<int, ObjectCtrlInfo> loadedItems)
        {
            if (operation == SceneOperationKind.Clear) return;

            var data = GetExtendedData();
            if (data == null || !data.data.ContainsKey(KEY)) return;

            var raw = data.data[KEY] as string;
            if (string.IsNullOrEmpty(raw)) return;

            // 依 dicKey 分組
            var byKey = new Dictionary<int, List<string>>();
            foreach (var line in raw.Split('\n'))
            {
                if (line.Length == 0) continue;
                int tab = line.IndexOf('\t');
                if (tab < 0) continue;

                int key;
                if (!int.TryParse(line.Substring(0, tab), out key)) continue;

                List<string> lines;
                if (!byKey.TryGetValue(key, out lines))
                {
                    lines = new List<string>();
                    byKey[key] = lines;
                }
                lines.Add(line.Substring(tab + 1));
            }

            // loadedItems 會把「存檔時的 key」對應到「這次載入後的實際物件」，
            // 所以匯入(Import)造成 key 位移也不會錯亂
            foreach (var kv in byKey)
            {
                ObjectCtrlInfo oci;
                if (!loadedItems.TryGetValue(kv.Key, out oci)) continue;

                var ociChar = oci as OCIChar;
                if (ociChar == null) continue;

                BlendShapeLock.Deserialize(ociChar.charInfo, string.Join("\n", kv.Value.ToArray()));
            }
        }
    }
}
