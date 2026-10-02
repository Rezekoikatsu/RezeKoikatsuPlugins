# -*- coding: utf-8 -*-
"""kkref.py — 場景卡裡所有「物件參照」的位置與座標系

這份是 kkscenemerge 的核心資料表。用實卡（場景1 / 場景2 / 手動合併結果）
逐欄位驗證過，驗證方法是：拿 A、B 兩張卡的參照值去預測合併卡的參照值，
預測集合必須跟實際集合完全相同。

== 兩種座標系 ==

RANK   物件的 dicKey 在「全部節點 dicKey 由小到大排序」裡的名次（0 起算）。
       不是 dicKey 本身 —— HANDOFF 裡寫的 objectIndex(=dicKey) 是錯的。
       只要節點集合有變（刪東西、合併），整個 RANK 空間就要重算。
       實卡驗證：合併卡 402 個 objectIndex，用 RANK 假設預測 402/402 全中。

DICKEY 直接就是 dicKey。

骨架 / IK target 的 dicKey 跟節點共用同一個編號空間（角色節點 dicKey=46，
骨架就從 47 開始），所以重編號時要一起加；但 RANK 只算「節點」，不算骨架。
"""

# ---------------------------------------------------------------- RANK 空間
RANK_REFS = [
    # (guid, payload key, 種類, 說明)
    ("timeline", "sceneInfo", "xml-attr:objectIndex",
     "每條 interpolable 指向的物件"),
    ("nodesConstraints", "constraints", "xml-attr:parentObjectIndex,childObjectIndex",
     "每條 constraint 的父/子物件"),
    ("rendererEditor", "xml", "xml-attr:objectIndex",
     "RendererEditor 每個 <renderer> 指向的物件（Silverwolf 原卡驗證：10/22/152 = 螢幕、兩個同款物件）"),
]

# ---------------------------------------------------------------- DICKEY 空間
DICKEY_REFS = [
    ("kkpe", "sceneInfo", "xml-attr:index", "每個 <object> 的 index"),
    ("org.njaecha.plugins.treenodenaming", "names", "msgpack-map-key",
     "每個節點的顯示名稱，鍵 = dicKey（節點數 == 筆數）"),
    ("com.deathweasel.bepinex.materialeditor", "*PropertyList", "msgpack-list-field:ID",
     "RendererPropertyList / MaterialFloatPropertyList / MaterialColorPropertyList /"
     " MaterialKeywordPropertyList / MaterialShaderList / MaterialTexturePropertyList /"
     " MaterialCopyList / ProjectorPropertyList"),
    ("com.bepis.sideloader.universalautoresolver", "itemInfo", "msgpack-list-field:SceneDicKey",
     "sideloader 物件解析表"),
    ("LightSettingsData", "LightSettingsData_lights", "msgpack-list-field:ObjectId",
     "每盞燈的進階設定"),
    ("com.rikkibalboa.bepinex.savecameraobjectfov", "cameras", "msgpack-map-key",
     "每台相機記住的 FOV"),
    ("keelhauled.itemlayeredit", "SavedLayers", "msgpack-list-field:ObjectId",
     "物件的 render layer"),
    ("keelhauled.realpov", "PovData", "msgpack-field:CharaId",
     "POV 綁定的角色"),
    ("org.njaecha.plugins.objimport", "ids", "msgpack-list（跟 meshes 同序）",
     "OBJImport：ids[i] 這個物件換成 meshes[i] 的匯入網格"),
    ("RSkoi_ComponentUtil", "*", "msgpack-map-key",
     "SceneCustomFunctionController Zoo / _addedComponents / _referenceProperties"),
]

# 貼圖字典另外一套：TexID 指向 TextureDictionary 的鍵
TEXTURE_REFS = [
    ("com.deathweasel.bepinex.materialeditor", "MaterialTexturePropertyList",
     "msgpack-list-field:TexID", "指向 TextureDictionary 的鍵"),
]

# ---------------------------------------------------------------- 特殊行為
NOTES = {
    "com.shallty.shalltyutils": (
        "guideObjectPickerData 帶 objectIndex，但 Studio 導入場景時是「整份被"
        "導入那張蓋掉」，底卡的會不見。座標系也對不上 RANK 或 DICKEY —— "
        "推測是另一套 UI 頁面索引。工具預設保留底卡的並警告。"),
    "RSkoi_ComponentUtil": (
        "跟 shallty 一樣，實卡上是被導入那張整份蓋掉（底卡的 1 筆不見了）。"
        "工具改成兩邊合併 + 重編號。"),
    "timeline-global": (
        "owner=\"Timeline\" 且沒有 objectIndex 的軌道 = 全域軌道，"
        "整張卡只有三條：timeScale / cameraFOV / cameraOZoom。"
        "Studio 導入時會把被導入那張的這三條丟掉，只留底卡的 —— "
        "這就是手動流程要先『貼上軌道數據』的原因。"
        "工具會把兩邊的關鍵影格（第二張先平移）合成一條。"),
    "textures": (
        "TextureDictionary 在導入時會「按內容去重」。實卡驗證："
        "1649 + 765 - 537(重複) = 1877 筆，位元組數 500,938,084 完全吻合。"),
}

# ---------------------------------------------------------------- 外太空停放
# 包裝資料夾的 guideObjectPos 軌道，曲線是階梯（inTangent="INF"）。
#   場景1（先播）： t=0 (0,0,0) -> t=自己的時長 (100,100,100)
#   場景2（後播）： t=0 (100,100,100) -> t=位移量 (0,0,0) -> t=總時長 (100,100,100)
PARK_CURVE = ('<curveKeyframe time="0" value="0" inTangent="0" outTangent="0" />'
              '<curveKeyframe time="1" value="1" inTangent="INF" outTangent="0" />')
PARK_POS = 100.0

GLOBAL_TIMELINE_IDS = ("timeScale", "cameraFOV", "cameraOZoom")

ME_LISTS = ("RendererPropertyList", "ProjectorPropertyList",
            "MaterialFloatPropertyList", "MaterialKeywordPropertyList",
            "MaterialColorPropertyList", "MaterialTexturePropertyList",
            "MaterialShaderList", "MaterialCopyList")
