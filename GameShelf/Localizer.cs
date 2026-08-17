using System.Globalization;

namespace GameShelf;

public sealed class Localizer
{
    private readonly string _language;
    private static readonly Dictionary<string, string[]> Text = new()
    {
        ["Library"] = ["Library", "ライブラリ", "遊戲庫", "游戏库"], ["Add"] = ["Add", "追加", "新增", "新增"],
        ["Delete"] = ["Delete", "削除", "刪除", "删除"], ["Edit"] = ["Edit", "編集", "編輯", "编辑"],
        ["Save"] = ["Save", "保存", "儲存", "保存"], ["Cancel"] = ["Cancel", "キャンセル", "取消", "取消"],
        ["Back"] = ["Back", "戻る", "返回", "返回"], ["Import"] = ["Import", "インポート", "匯入", "导入"],
        ["Export"] = ["Export", "エクスポート", "匯出", "导出"], ["Settings"] = ["Settings", "設定", "設定", "设置"],
        ["Game management"] = ["Game management", "ゲーム管理", "遊戲管理", "游戏管理"],
        ["Global settings"] = ["Global settings", "グローバル設定", "全域設定", "全局设置"],
        ["No filter"] = ["No filter", "フィルターなし", "不篩選", "不筛选"], ["Choose image"] = ["Choose image", "画像を選択", "選擇圖片", "选择图片"],
        ["Play"] = ["Play", "開始", "啟動", "启动"], ["Missing path"] = ["Missing or invalid game executable", "ゲーム実行ファイルが未設定または無効です", "遊戲執行檔未設定或無效", "游戏可执行文件未设置或无效"],
        ["Confirm deletion"] = ["Confirm deletion", "削除の確認", "確認刪除", "确认删除"], ["none"] = ["none", "なし", "無", "无"],
        ["Theme"] = ["Theme", "テーマ", "主題", "主题"], ["Language"] = ["Language", "言語", "語言", "语言"],
        ["Dimensions"] = ["Dimensions", "次元", "標籤維度", "标签维度"], ["Region commands"] = ["Region commands", "地域コマンド", "轉區指令", "转区指令"],
        ["Statuses"] = ["Statuses", "ステータス", "狀態", "状态"], ["Select a game"] = ["Select a game", "ゲームを選択", "選擇遊戲", "选择游戏"]
    };
    public Localizer(string saved) { _language = "en"; }
    public string this[string key] => Text.TryGetValue(key, out var s) ? s[_language switch { "ja" => 1, "zh" when CultureInfo.CurrentUICulture.Name.Contains("Hans") => 3, "zh-CN" => 3, "zh-Hans" => 3, "zh" => 2, _ => 0 }] : key;
}
