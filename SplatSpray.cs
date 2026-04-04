using Oxide.Core;
using Oxide.Game.Rust.Cui;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("SplatSpray", "jerky+claude", "1.0.0")]
    [Description("Splat paintball spray system")]
    class SplatSpray : RustPlugin
    {
        private const ulong SPRAY_SKIN_ID = 3699340642;
        private const string SPRAY_PREFAB = "assets/prefabs/tools/spraycan/sprays/spray.decal.prefab";
        private string _sprayPrefab = SPRAY_PREFAB;

        // ゲームパラメータ
        private const float CLUSTER_RADIUS = 2.0f;
        private const int HITS_TO_SPRAY = 5;
        private const int MAX_TRACKED_HITS = 30;

        // スケール: 通常5倍、レア(10%)で8倍
        private const float SCALE_NORMAL = 5f;
        private const float SCALE_RARE   = 8f;
        private const float RARE_CHANCE  = 0.1f;

        // ゲージパラメータ
        private const float GAUGE_PER_HIT   = 0.1f;
        private const float GAUGE_PER_SPRAY = 5f;
        private const float UI_UPDATE_INTERVAL = 0.1f;

        private struct HitData
        {
            public Vector3 pos;
            public Vector3 normal;
        }

        private readonly Dictionary<ulong, List<HitData>> _hits    = new Dictionary<ulong, List<HitData>>();
        private readonly Dictionary<ulong, float>         _gauge   = new Dictionary<ulong, float>();
        private readonly HashSet<ulong>                   _cleared = new HashSet<ulong>();
        private Timer _tickTimer;

        // ---- ライフサイクル ----

        void OnServerInitialized()
        {
            foreach (var player in BasePlayer.activePlayerList)
                InitPlayer(player);

            _tickTimer = timer.Every(UI_UPDATE_INTERVAL, OnTick);
        }

        void Unload()
        {
            _tickTimer?.Destroy();
            foreach (var player in BasePlayer.activePlayerList)
            {
                CuiHelper.DestroyUi(player, "SplatGauge");
                CuiHelper.DestroyUi(player, "SplatClear");
            }
        }

        void OnPlayerConnected(BasePlayer player) => InitPlayer(player);

        void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            CuiHelper.DestroyUi(player, "SplatGauge");
            _hits.Remove(player.userID);
            _gauge.Remove(player.userID);
        }

        void InitPlayer(BasePlayer player)
        {
            _hits[player.userID]  = new List<HitData>();
            _gauge[player.userID] = 0f;
            _cleared.Remove(player.userID);
            DrawGauge(player);
        }

        // ゲージ加算の一本化 → ここで100%到達を検知
        void AddGauge(BasePlayer player, float amount)
        {
            if (_cleared.Contains(player.userID)) return; // クリア済みは加算しない

            float prev = _gauge[player.userID];
            _gauge[player.userID] = Mathf.Min(100f, prev + amount);
            DrawGauge(player);

            if (_gauge[player.userID] >= 100f)
            {
                _cleared.Add(player.userID);
                ShowGameClear(player);
            }
        }

        // ---- ペイントボール着弾検出 ----

        void OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            if (info?.Weapon == null || info.InitiatorPlayer == null) return;
            if (!(info.Weapon is PaintballGun)) return;
            OnPaintballHit(info.InitiatorPlayer, info.HitPositionWorld, info.HitNormalWorld);
        }

        void OnPlayerAttack(BasePlayer attacker, HitInfo info)
        {
            if (attacker == null || info == null) return;
            if (!(attacker.GetHeldEntity() is PaintballGun)) return;
            if (info.HitEntity != null) return;
            if (info.HitPositionWorld == Vector3.zero) return;
            OnPaintballHit(attacker, info.HitPositionWorld, info.HitNormalWorld);
        }

        void OnPaintballHit(BasePlayer player, Vector3 pos, Vector3 normal)
        {
            if (!_hits.ContainsKey(player.userID)) InitPlayer(player);
            if (normal == Vector3.zero) normal = Vector3.up;

            var hits = _hits[player.userID];
            hits.Add(new HitData { pos = pos, normal = normal });
            if (hits.Count > MAX_TRACKED_HITS) hits.RemoveAt(0);

            AddGauge(player, GAUGE_PER_HIT);

            CheckCluster(player, hits, pos);
        }

        void CheckCluster(BasePlayer player, List<HitData> hits, Vector3 recentHit)
        {
            var nearby = hits.Where(h => Vector3.Distance(h.pos, recentHit) <= CLUSTER_RADIUS).ToList();
            if (nearby.Count < HITS_TO_SPRAY) return;

            var used = nearby.Take(HITS_TO_SPRAY).ToList();

            Vector3 center = Vector3.zero;
            Vector3 avgNormal = Vector3.zero;
            foreach (var h in used) { center += h.pos; avgNormal += h.normal; }
            center    /= used.Count;
            avgNormal  = (avgNormal / used.Count).normalized;

            foreach (var h in used) hits.Remove(h);

            CreateSpray(player, center, avgNormal);
        }

        // ---- スプレー生成 ----

        void CreateSpray(BasePlayer player, Vector3 position, Vector3 normal)
        {
            // SprayCan.cs と同じ回転計算
            Vector3 eyePos  = player.eyes.position;
            Vector3 tangent = (new Plane(normal, position).ClosestPointOnPlane(eyePos) - position).normalized;
            if (tangent.sqrMagnitude < 0.001f)
                tangent = Vector3.Cross(normal, Vector3.right).normalized;
            Quaternion rot = Quaternion.LookRotation(tangent, normal);
            rot *= Quaternion.Euler(0f, 0f, 90f);

            // 通常5倍、10%の確率でレア8倍
            bool isRare = UnityEngine.Random.value < RARE_CHANCE;
            float scale = isRare ? SCALE_RARE : SCALE_NORMAL;

            var ent = GameManager.server.CreateEntity(_sprayPrefab, position, rot);
            if (ent == null)
            {
                Puts($"[SplatSpray] スプレーエンティティ生成失敗: {_sprayPrefab}");
                return;
            }

            ent.skinID = SPRAY_SKIN_ID;
            ent.OwnerID = player.userID;
            ent.networkEntityScale = true;
            ent.transform.localScale = Vector3.one * scale;
            ent.OnDeployed(null, player, null);
            ent.Spawn();

            AddGauge(player, GAUGE_PER_SPRAY);
        }

        // ---- UIタイマー（ゲージ減少なし・再描画のみ）----

        void OnTick()
        {
            // 現在は減少処理なし（コンセプト版）
        }

        // ---- ゲームクリアスプラッシュ ----

        void ShowGameClear(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, "SplatClear");

            var c = new CuiElementContainer();

            // 半透明フルスクリーン背景
            c.Add(new CuiPanel
            {
                Image = { Color = "0.05 0.05 0.15 0.75" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                CursorEnabled = false
            }, "Overlay", "SplatClear");

            // GAME CLEAR 大文字
            c.Add(new CuiLabel
            {
                Text =
                {
                    Text = "GAME CLEAR!",
                    FontSize = 72,
                    Align = TextAnchor.MiddleCenter,
                    Color = "0.2 0.95 1.0 1.0"
                },
                RectTransform = { AnchorMin = "0.1 0.5", AnchorMax = "0.9 0.75" }
            }, "SplatClear");

            // サブテキスト
            c.Add(new CuiLabel
            {
                Text =
                {
                    Text = "You painted the town!",
                    FontSize = 26,
                    Align = TextAnchor.MiddleCenter,
                    Color = "1.0 1.0 1.0 0.85"
                },
                RectTransform = { AnchorMin = "0.1 0.38", AnchorMax = "0.9 0.52" }
            }, "SplatClear");

            // デコライン（上）
            c.Add(new CuiPanel
            {
                Image = { Color = "0.2 0.85 1.0 0.6" },
                RectTransform = { AnchorMin = "0.2 0.76", AnchorMax = "0.8 0.78" }
            }, "SplatClear");

            // デコライン（下）
            c.Add(new CuiPanel
            {
                Image = { Color = "0.2 0.85 1.0 0.6" },
                RectTransform = { AnchorMin = "0.2 0.36", AnchorMax = "0.8 0.38" }
            }, "SplatClear");

            CuiHelper.AddUi(player, c);

            // 5秒後に自動で消してゲージをリセット
            timer.Once(5f, () =>
            {
                if (player == null || !player.IsConnected) return;
                CuiHelper.DestroyUi(player, "SplatClear");
                _gauge[player.userID] = 0f;
                _cleared.Remove(player.userID);
                DrawGauge(player);
            });
        }

        // ---- コマンド ----

        [ChatCommand("splatreset")]
        void CmdReset(BasePlayer player, string command, string[] args)
        {
            if (!_gauge.ContainsKey(player.userID)) return;
            _gauge[player.userID] = 0f;
            _cleared.Remove(player.userID);
            CuiHelper.DestroyUi(player, "SplatClear");
            DrawGauge(player);
        }

        // ---- ゲージUI（上部表示）----

        void DrawGauge(BasePlayer player)
        {
            float pct = _gauge.ContainsKey(player.userID) ? _gauge[player.userID] / 100f : 0f;

            CuiHelper.DestroyUi(player, "SplatGauge");

            var c = new CuiElementContainer();

            // ── ルートパネル（画面上部中央）
            c.Add(new CuiPanel
            {
                Image = { Color = "0 0 0 0" },
                RectTransform = { AnchorMin = "0.35 0.88", AnchorMax = "0.65 0.98" },
                CursorEnabled = false
            }, "Hud", "SplatGauge");

            // ── タイトル
            c.Add(new CuiLabel
            {
                Text =
                {
                    Text = "■ SPLAT METER ■",
                    FontSize = 13,
                    Align = TextAnchor.MiddleCenter,
                    Color = "1 1 1 1"
                },
                RectTransform = { AnchorMin = "0 0.72", AnchorMax = "1 1" }
            }, "SplatGauge");

            // ── バー外枠
            c.Add(new CuiPanel
            {
                Image = { Color = "1 1 1 0.25" },
                RectTransform = { AnchorMin = "0.0 0.05", AnchorMax = "1.0 0.68" }
            }, "SplatGauge", "SplatGauge_Outer");

            // ── バー内側背景
            c.Add(new CuiPanel
            {
                Image = { Color = "0.05 0.05 0.1 0.9" },
                RectTransform = { AnchorMin = "0.005 0.05", AnchorMax = "0.995 0.95" }
            }, "SplatGauge_Outer", "SplatGauge_BarBG");

            // ── バー塗り（水色固定）
            if (pct > 0.01f)
            {
                float fillRight = Mathf.Clamp(pct, 0.005f, 0.995f);

                c.Add(new CuiPanel
                {
                    Image = { Color = "0.2 0.85 1.0 1.0" },
                    RectTransform = { AnchorMin = "0.002 0.04", AnchorMax = $"{fillRight:F3} 0.96" }
                }, "SplatGauge_BarBG", "SplatGauge_Fill");

                // バー右端ハイライト
                if (pct > 0.04f)
                {
                    float glowLeft = Mathf.Max(0f, fillRight - 0.04f);
                    c.Add(new CuiPanel
                    {
                        Image = { Color = "1 1 1 0.35" },
                        RectTransform = { AnchorMin = $"{glowLeft:F3} 0.04", AnchorMax = $"{fillRight:F3} 0.96" }
                    }, "SplatGauge_BarBG");
                }
            }

            CuiHelper.AddUi(player, c);
        }
    }
}
