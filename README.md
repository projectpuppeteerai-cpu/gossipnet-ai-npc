# GossipNet — AI NPC Core Engine

**プレイヤーが話しかける前に、セリフはもう出来ている。**

GossipNetは、プレイヤー行動をトリガーに裏側の非同期ワーカーがNPCのセリフ・行動候補を
LLMで先回り生成・キャッシュし、ゲーム側へ構造化JSONとして配信するUnity用ミドルウェアです。
「話しかけた瞬間にAPIを叩いて応答を待つ」設計とは異なり、**会話が発生しうるタイミングより前に
非同期でキャッシュを埋めておく**ことで、体感レイテンシとAPIコストの両方を削減します。

## 特徴

- **非同期先読みキャッシュ**: モブNPCは共有プール、主要NPCは個体別キャッシュテーブルを持ち、
  バッチ生成（1回の問い合わせで複数件まとめて生成）で不足分を補充。話しかけるたびに
  API往復を待つ必要がありません
- **赤・黄・緑のUIステート制御**: `NpcStateWatcher`が`Unprepared`（未準備）/`Updating`
  （生成中）/`Ready`（会話可能）の3状態を監視・通知し、プレイヤーに「話しかけられるか」を
  視覚的に伝えられます
- **薄いミドルウェア設計**: GossipNetが担うのは「キャッシュテーブル」「コンテキスト設定」
  「反映（ディスパッチ）」の3点のみ。コンテキストが変化した後、いつキャッシュを更新するかは
  ゲーム側の裁量に委ねられており、ミドルウェアが勝手に判断しません
- **NPCごとの「知っていること」**: 全員共通の行動履歴ではなく、NPCごとに「自分の目で見た／
  音だけで知った／人から聞いた」事実を持たせ、セリフ生成にはそのNPC自身の知識だけを使います。
  その場に居合わせなかったNPCが出来事に触れてしまう不自然さを避けられます
- **Appraisal（出来事の知覚・感情判定、任意）**: 出来事を知覚したNPCが、どう感じ・どれくらい警戒するかを
  LLMで構造化JSONとして判定し、口調・反応方針としてセリフ生成へ反映します
- **OpenAI互換 / Anthropic 両対応**: `LLMBridge`が`ApiProvider`の切り替えで両方の
  APIエンドポイントに対応。Anthropicでは単発生成にStructured Outputsを利用し、
  スキーマ通りのJSONを強制します
- **オフライン再生対応**: 開発時に生成した会話をJSONへエクスポートし、リリース時は
  API通信0円で固定データのみ再生する運用が可能です

## 3つの運用モード

`AsyncCachePipeline`の`_mode`（Inspector設定）で切り替えます。

| モード | 挙動 |
|---|---|
| `DevelopmentExport` | 常に実LLM生成を行い、生成結果を都度`exported_conversations.json`へ自動保存（追記マージ）する。開発中に会話データを蓄積するためのモード |
| `StaticOffline` | 保存済みJSONのみを参照し、APIへは一切問い合わせない。API通信費0円でのリリース向け |
| `DynamicUserKey` | 通常のリアルタイム生成。ただしAPIキーが未設定の間は`StaticOffline`と同様に自動フォールバックする |

モードの切り替え（例: プレイヤーが自前のAPIキーを設定した後に`DynamicUserKey`へ移行する等）は
`AsyncCachePipeline.SetMode()` / `LLMBridge.SetApiKey()`を呼ぶ側（利用者）の裁量です。

## クイックスタート

### 1. UPM経由でのインポート

Unity Editorの `Window > Package Manager` から `+` > `Add package from git URL...` を選び、
本パッケージのGitリポジトリURLを指定してください。ローカルのファイルパスから
`Add package from disk...`（`package.json`を選択）で導入することも可能です。

依存パッケージ（`com.unity.nuget.newtonsoft-json`）は`package.json`の記述に従い
自動的に解決されます。

サンプル（デモシーン構築ツール・利用例スクリプト）は自動インポートされません。
`Package Manager` の対象パッケージ詳細画面から `Samples > Import` を押すと、
プロジェクトへオプトインでコピーされます。

### 2. APIキーの設定

`Api Key`をInspectorへ直書きするとビルド成果物に埋め込まれ第三者に抽出されうるため、
実行ファイルと同階層（Editorでは`Application.dataPath`の一つ上）に置く
設定ファイル方式を採用しています。

1. 同梱の`gossipnet_config.json.example`をコピーし、`gossipnet_config.json`にリネーム
2. 中の`apiKey`に実際のAPIキーを記入

```json
{
  "apiKey": "sk-ant-xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"
}
```

`gossipnet_config.json`は**絶対にリポジトリへコミットしないでください**（`.gitignore`で
除外済み）。設定ファイルが無い/読めない場合はInspectorの値を使用し、それも空なら
`DynamicUserKey`モードは自動的にオフラインフォールバックするため、未設定でもエラー落ちは
しません。

## 基本的な使い方

### `ContextManager` へのアクション記録

プレイヤーの行動やワールドの状態変化を記録すると、以後のNPC応答生成のコンテキストに
反映されます。

```csharp
using AINPCCoreEngine;

// プレイヤーが特筆すべき行動を取ったときに記録
ContextManager.Instance.RecordAction(actionType: "壺をこわした", target: "町の壺");

// 町の特色など、ワールド側の恒常的な情報を設定
ContextManager.Instance.SetWorldFlag(key: "town_mood", value: "祭りの直前で浮足立っている");

// プレイヤー位置・周辺NPCの更新
ContextManager.Instance.UpdatePosition(currentPos, locationTag: "market_square");
```

**「コンテキストを更新したら、いつキャッシュへ反映させるか」はミドルウェア側では判断しません。**
更新後、必要なタイミングで明示的に`AsyncCachePipeline`の手動更新APIを呼び出してください。

### `NpcStateWatcher` の購読

NPCと同じGameObjectに`NpcStateWatcher`をアタッチすると、会話準備状態
（`Unprepared` / `Updating` / `Ready`）の変化を通知してくれます。見た目（色・エフェクト等）は
一切持たないため、自由な表現に差し替え可能です。

```csharp
using AINPCCoreEngine;
using UnityEngine;

public class MyNpcIndicator : MonoBehaviour
{
    [SerializeField] private NpcStateWatcher _watcher;

    private void OnEnable() => _watcher.OnStateChanged += HandleStateChanged;
    private void OnDisable() => _watcher.OnStateChanged -= HandleStateChanged;

    private void HandleStateChanged(NpcConversationState state)
    {
        switch (state)
        {
            case NpcConversationState.Unprepared: /* 赤: 未準備 */ break;
            case NpcConversationState.Updating:   /* 黄: 生成中 */ break;
            case NpcConversationState.Ready:      /* 緑: 会話可能 */ break;
        }
    }
}
```

### `AsyncCachePipeline` からの会話消費

`Ready`状態のNPCに話しかけると、キャッシュ済みの応答をFIFOで1件取り出せます。

```csharp
using AINPCCoreEngine;

public void TalkTo(string npcId, AsyncCachePipeline pipeline)
{
    if (pipeline.GetConversationState(npcId) != NpcConversationState.Ready)
    {
        // 未準備または生成中。話しかけを成立させない
        return;
    }

    CachedResponse cached = pipeline.ConsumeCache(npcId);
    NpcResponseSchema response = cached.Response;

    Debug.Log($"{response.npc_id}: {response.dialogue} ({response.emotion})");
    // ActionDispatcherへ渡してセリフ/行動/アニメーションへ反映する
    actionDispatcher.Dispatch(response);

    // 消費した1件を裏で自動補充（forceFullRefresh: false）
    _ = pipeline.PrefetchForNpcAsync(npcId);
}
```

## NPCごとの知識（誰が何を知っているか）

`ContextManager`の`Use Npc Knowledge`（Inspector）をオンにすると、セリフ生成プロンプトに
**全NPC共通の行動履歴（`RecentActions`）の代わりに、そのNPC自身が知っている事実**が載ります。
`RecentActions`は引き続き保持されるため、プレイヤー行動の記録・イベントフラグとして使えます
（プロンプトに載らなくなるだけです）。既定はオフで、オフなら従来どおり共通履歴が載ります。

**誰がいつ何を知るか（視覚・聴覚の判定、伝聞の伝播など）はミドルウェアは判断しません。**
ゲーム側で判定し、知るべきNPCに対して`LearnFact`を呼んでください。

```csharp
using AINPCCoreEngine;

var ctx = ContextManager.Instance;

// 衛兵はその出来事を自分の目で見た
ctx.LearnFact("guard", "強盗事件を目撃した", "路地裏の強盗", FactSource.Witnessed);

// 別の衛兵は物音だけ聞こえた（セリフでは断定を避け、疑い混じりの話し方になる）
ctx.LearnFact("guard_far", "壺をこわした", "町の壺", FactSource.HeardOnly);

// 他のNPCから聞いた（伝聞）
ctx.LearnFact("villager_x", "強盗事件を目撃した", "路地裏の強盗", FactSource.ToldBy, toldByNpcId: "guard");

// 町で広く知られていること。モブ共有プール（Tier.Mob）がセリフ生成に使う知識
ctx.LearnPublicFact("壺をこわした", "町の壺");
```

- 主要NPCのプロンプトには、**本人の知識だけ**が載ります（公開情報は自動では混ざりません）
- モブ共有プールは個体を持たないため、**公開情報**（`LearnPublicFact`）だけを参照します
- 1体あたりの保持件数は`Max Facts Per Npc`（既定10）。`ClearNpcFacts` / `ClearAllKnowledge`で消去できます

## Appraisal: 出来事の知覚・感情判定（任意）

> **注意: Appraisal は「プロンプトによる判定」であり、決定モデル（Decision Model）ではありません。**
> Appraisal は、汎用のLLMへプロンプトで「このNPCならどう感じるか」を答えさせ、JSONで受け取る
> 仕組みです。判定専用に作られたモデルではないため、結果は確率的にぶれ（同じ入力でも変わりえます）、
> 品質はプロンプトの指示と、コード側の保険（脅威度の変化幅の制限など）で保っています。判定の
> たびにLLM APIの呼び出し時間と費用もかかります。
>
> 近年、選択肢・確率・スコアを返す**決定モデル**が実用化されています（例: Ollama 0.35.0 が
> TypeSafe の「Jev API」を元に対応した `/v1/systemone`）。**「JEV」という名称は、この決定モデル
> による判定のために空けてあります。** 本パッケージは、**決定モデルによる JEV を、近い将来ミドル
> ウェアに組み込む予定**です（時期は未定。現時点では未対応です）。組み込み後は、プロンプトによる
> Appraisal と、決定モデルによる JEV を使い分けられるようにする想定です。

出来事（`AppraisalEventContext`: 種類・距離・視認できたか・音だけか）をNPCが知覚したとき、そのNPCが
どう感じ（`emotion_state`）、どれくらい警戒し（`threat_level`）、どう反応するか
（`reaction_category`）をLLMで判定し、`ContextManager`のNPC別ストアへ保存します。保存された
結果は、そのNPCのセリフ生成に口調・反応方針として反映されます。使わなくても他の機能は動きます。

```csharp
using AINPCCoreEngine;

var evaluator = FindFirstObjectByType<AppraisalEvaluator>();

await evaluator.EvaluateAsync("guard", new AppraisalEventContext
{
    event_type = "robbery_witnessed",
    relative_distance_meters = 6f,
    has_line_of_sight = true,
    heard_only = false,
    // プレイヤー以外が起こした事件を、プレイヤーが居合わせて目撃した場合
    actor = AppraisalEventContext.ActorThirdParty,
});
```

- `heard_only = true`なら「音だけで知覚した」扱いになり、断定を避けた疑い混じりの反応になります
- `actor`を`ActorThirdParty`にすると、プレイヤーを加害者・容疑者として扱わず（糾弾・投降要求をしない）、
  怒りも選ばず、脅威度も低く抑えます。未指定ならプレイヤー本人の行動として判定します
- 複数の出来事は、直前の心情を引き継いで合成されます。`AppraisalEvaluator`の`Max Threat Step Per Event`
  （既定0.35）で、1回の判定で脅威度が急変しないよう抑えています
- 判定結果には**自動失効がありません**。クリアする（`ClearNpcAppraisalState`）タイミングもゲーム側の裁量です
- 誰に判定させるか（距離・視野角・障害物など）はゲーム側で絞り込んでください。サンプルの
  `AppraisalPerceptionPreFilter`が距離・視線チェックの一例です

## 向いているゲーム・向いていないゲーム

GossipNetは「プレイヤー行動→裏で非同期にLLM生成・キャッシュ→話しかけた瞬間にキャッシュから
即座に返す」設計です。**LLMの応答生成そのものは決して速くありません**。実機検証では、
1回のバッチ生成件数を1件まで絞ってもレイテンシはほぼ改善せず、支配的なコストは出力トークン量
ではなくAPI呼び出し自体の固定オーバーヘッド（ネットワーク往復・キュー待ち等）でした。
つまり「生成量を減らせば速くなる」性質のボトルネックではありません。

- **向いている**: テーブルトークRPG風の会話劇、『Slay the Spire』のような
  ターン制・デッキ構築系、アドベンチャー/ビジュアルノベルなど、**プレイヤーの意思決定と
  NPCの反応の間に自然な「間」があるゲーム**。イベント発生から実際にNPCの反応が必要になる
  までの数百ms〜数秒を先読み生成に充てられるため、体感の待ち時間をほぼ隠蔽できます
- **注意が必要**: アクション性・リアルタイム性が強いゲーム。イベント発生の直後に
  即座に反応セリフが欲しい場面では、`NpcStateWatcher`が`Updating`（生成中）を報告している
  間は会話が不成立になるか、`NpcPersona.DefaultDialogue`／呼び出し単位の
  `fallbackDialogueOverride`によるフォールバック文言で埋めることになります。加えて
  `AsyncCachePipeline`は生成中（`_inFlight`）の間、既存キャッシュが残っていても
  `Updating`判定を優先する設計のため、「古いキャッシュだけでも即座に返す」高速化はできません
  （既存の状態遷移仕様を維持する設計判断として、緩和は見送られています）

格闘ゲームの決着後の一言、弾幕を掻い潜りながらの即時リアクションのような、遅延が許されない
場面では、GossipNetの非同期キャッシュに頼らず固定セリフ・短い定型パターンを併用することを
推奨します。

## ライセンス・権利表記について

- 本パッケージ（AI NPC Core Engine、コードネーム: GossipNet）のソースコードは
  [MITライセンス](LICENSE)で提供されます
- 依存ライブラリのライセンスについては[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)を
  参照してください
- 本パッケージ自体はLLM APIの利用契約・APIキーを含みません。OpenAI互換API /
  Anthropic APIの利用にあたっては、各サービスの利用規約・料金体系に従い、
  利用者自身のAPIキーで運用してください
- サンプルシーン・サンプルスクリプト（`Samples~`）はUPMの標準機構に従いオプトイン導入され、
  そのまま製品に組み込むことを想定した実装ではありません（デモ・学習目的）
- 本パッケージの利用によって生成されるNPCの発話内容は、使用するLLMモデル・プロンプト・
  外部APIサービスの応答に依存します。生成内容の事前検証（不適切な発話のフィルタリング等）は
  利用者の責任で行ってください
