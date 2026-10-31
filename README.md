# imageviewer

Windows 11 用の、縁なしの画像ビューアです。ウィンドウの大きさが画像の大きさと同じになります。

## 使い方

```
imageviewer.exe [画像ファイル]
```

- 引数なしで起動すると、ファイル選択画面が出ます。
- ウィンドウに画像をドラッグ&ドロップしても開けます。

| 操作 | 動き |
|---|---|
| 左ドラッグ | ウィンドウを動かす |
| ホイール | 拡大縮小(カーソルの位置が中心、1 目盛り 1.1 倍) |
| → / ← | 同じフォルダの、更新日が新しい / 古い画像に切り替え(端まで行くと反対の端に戻る) |
| 右クリック | 拡大率(100% に戻すボタン付き)・ファイル名・フルパス・ペイントで開く のビュー |
| Esc | 閉じる |

最初のウィンドウの大きさ:

- 縦も横も 32 px 未満: 長い辺が 512 px になるように拡大
- 横 1920 px 以上 または 縦 1080 px 以上: 1920x1080 に収まるように縮小
- それ以外: 画像と同じ大きさ(画像の 1 px = 画面の 1 px)

## ビルド

必要なもの: Visual Studio 2026(または Build Tools)と .NET Framework 4.8.1 ターゲット パック。

- Windows: `build.bat`
- WSL: `./build.sh`

出力: `bin\Release\imageviewer.exe`(.NET Framework 4.8.1 は Windows 11 に最初から入っているので、exe 1 つで動きます)

Release ビルドのあと、exe は `C:\app\imageviewer\imageviewer.exe` にコピーされます。こちらを使ってください。WSL の中(`\\wsl.localhost\...`)の exe を直接動かすと、Windows が「作成者を確認できません」の確認を毎回出します。

- コピー先を変える: `-p:DeployDir=D:\tools\imageviewer\`
- コピーしない: `-p:DeployDir=none`

## ファイル

- `src/Program.cs` 起動と引数
- `src/ViewerWindow.cs` メインのウィンドウ(大きさ、ドラッグ、ホイール、左右キー)
- `src/InfoPopup.cs` 右クリックビュー
- `src/ImageLoader.cs` 画像の読み込み、EXIF の回転、フォルダ内の一覧
- `src/Native.cs` Win32 の呼び出し
- `icon.ico` アプリのアイコン。`python3 tools/make_icon.py` で作り直せます(Pillow と Consolas Bold を使います)
