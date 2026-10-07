# ExplorersIceboxJP v0.1.1

## 改変版・ライセンス表記

- Original project: **Explorers-Icebox** by **Ice / LeontopodiumNivale14**
- Modified version: **ExplorersIceboxJP**
- Modified by: **Elpa**
- Modification date for this release: **2026-10-07**
- License: **GNU Affero General Public License v3.0 or later (AGPL-3.0-or-later)**

本プロジェクトは Explorers-Icebox を改変した派生版であり、元プロジェクトと同じ AGPL-3.0-or-later の条件で公開します。
元のライセンス文は `LICENSE.md` に保持し、改変版の対応ソースコードも同じライセンス条件で提供します。

ExplorersIceboxJP は、毎日の無人島作業の手間を少しお手伝いすることを目的とした Dalamud プラグインです。
本家 Explorers-Icebox の通常採集をベースに、日本語UIと補助機能を追加しています。

## 主な機能

- 無人島の通常採集
- 収集リストによる目標数までの素材収集
- 畑の対象区画への水やり
- 牧場の対象動物への餌やり・収穫
- アイルデジョン／無人島への帰還補助
- 日本語UI

## 必要プラグイン

- **vnavmesh** — 採取・畑・牧場などの自動移動で使用
- **Lifestream** — 「無人島へ戻る」機能で使用

## コマンド

- `/explorersiceboxjp` — メイン画面を開く
- `/iceboxjp` — 短縮コマンド
- `/explorersiceboxjp s` または `/explorersiceboxjp settings` — 設定画面を開く

## 注意・未実装

- 飛行未解放時は、本家仕様により一部素材を自動採取できません。飛行可能になるまでは一部を手動で採取してください。
- 畑は現在、水やりのみ対応しています。収穫・種まきは未実装です。
- 畑のマメット管理・操作は対象外です。
- 牧場では動物ごとの個別餌設定、餌やり／収穫の個別指定には対応していません。

## クレジット / ライセンス

- Original: [LeontopodiumNivale14/Explorers-Icebox](https://github.com/LeontopodiumNivale14/Explorers-Icebox)
- JP custom: Elpa
- License: AGPL-3.0-or-later

元プロジェクトへの敬意とライセンス条件を維持した非公式改変版です。
