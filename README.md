# YMM4 高速PSD図形プラグイン（YMM4FastPsdShape）

作者: Tebasaki ／ ライセンス: [MIT License](LICENSE)

ゆっくりムービーメーカー4の図形の種類に「高速PSD」を追加するプラグインです。PSD を図形アイテムとして置き、アイテムごとにレイヤーの表示とプリセットを切り替えられます。
PSD の読み込みと合成は、YMM4 高速PSD立ち絵プラグイン（YMM4FastPsd）の仕組みを元にしています。

## ダウンロード

配布用の `YMM4FastPsdShape.ymme` は、GitHub の Releases からダウンロードできます。ダブルクリックすると YMM4 のプラグインインストーラーが開きます。
使い方・制限事項・更新履歴は [dist/README.txt](dist/README.txt) をご覧ください。

## フォルダ構成

```
src/FastPsd.Core/        YMM4に依存しない部分（PSD読み込み・表示状態の計算・合成手順・プリセットのファイル）
src/YMM4FastPsdShape/    プラグイン本体（FastPsd.Core のソースも取り込んで1つのDLLにする）
tests/                   テスト（FastPsd.Core.Tests）とテスト用PSDの作成スクリプト
dist/README.txt          配布物に入れる利用者向けの説明
tools/pack.ps1           配布用の .ymme を作るスクリプト（Windows。artifacts/ に出力）
```

## ビルド方法

1. `Directory.Build.props.sample` を `Directory.Build.props` という名前でコピーし、`YMM4DirPath` を YMM4 のフォルダに書き換えます（末尾の `\` を忘れずに）。
2. `src/YMM4FastPsdShape` で `dotnet build -c Release` を実行します。
3. `bin/Release/net10.0-windows/YMM4FastPsdShape.dll` ができます。

画面は XAML ファイルを使わずコードで作っており、WPF の DLL は YMM4 のフォルダから直接参照しています。そのため Windows 以外（Linux など）でも配布用の DLL を作れます（必要なのは .NET 10 SDK だけです）。

## テスト

```
cd tests/FastPsd.Core.Tests
dotnet run -c Release -- ../fixtures/rules.psd [合成テスト用PSDのフォルダ] [速度確認用PSD]
```

## 設定ファイル

プリセットは、PSD と同じフォルダの `<PSD名>-ymmfastpsdshape.json` に保存します（同じ PSD を使う図形で共通）。
高速PSD立ち絵の `<PSD名>-ymmfastpsd.json` は、プリセットの取り込みで読むだけで、変更しません。

## 配布用ファイルの作り方

Windows の PowerShell で、リポジトリのルートから次を実行すると `artifacts\YMM4FastPsdShape.ymme` ができます。

```
powershell -ExecutionPolicy Bypass -File tools\pack.ps1
```
