using Oxide.Core;
using Oxide.Game.Rust.Cui;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("SplatSpray", "jerky", "1.0.0")]
    [Description("Splat paintball territory game")]
    class SplatSpray : RustPlugin
    {
        // ---- プレハブ ----
        private const string SPRAY_PREFAB = "assets/prefabs/tools/spraycan/sprays/spray.decal.prefab";

        // ---- スプレー挙動 ----
        private const float CLUSTER_RADIUS      = 2.0f;  // 着弾クラスター判定半径(m)
        private const int   HITS_TO_SPRAY       = 5;     // 発動に必要な着弾数
        private const int   MAX_TRACKED_HITS    = 30;    // 追跡する最大着弾数
        private const float SCALE_NORMAL        = 5f;
        private const float SCALE_RARE          = 8f;
        private const float RARE_CHANCE         = 0.1f;
        private const float BASE_DECAL_RADIUS   = 0.5f;  // 1倍時のデカール半径(m) ※要調整
        private const float FLOOR_THRESHOLD     = 0.7f;  // 水平面とみなす法線Y閾値

        // ---- 面積計算 ----
        private const float CALC_STEP           = 0.3f;  // サンプリング間隔(m)
        private const float CALC_INTERVAL       = 5f;    // 計算実行間隔(秒)
        private const int   MAX_PAINT_HISTORY   = 500;   // 最大記録件数


        // ---- チームUI色（Unity RGBA）----
        private const string COLOR_A = "0.2 0.9 1.0 1.0";  // 水色
        private const string COLOR_B = "0.7 0.2 1.0 1.0";  // 紫

        // ---- 弾色インデックス → デカールスキンID ----
        // /splatcolorcheck で各色のインデックスを確認して設定してください
        private readonly Dictionary<int, ulong> _colorSkins = new Dictionary<int, ulong>
        {
            { 1, 3699340642 },  // 水色スキン
            { 4, 3700118804 },  // 紫スキン
        };

        // ---- 弾色インデックス → チームID ----
        // 水色(1) → Team A (0)、紫(4) → Team B (1)
        // このマップに含まれない色はスプレーが発動しない
        private readonly Dictionary<int, int> _colorToTeam = new Dictionary<int, int>
        {
            { 1, 0 },  // 水色 → Team A
            { 4, 1 },  // 紫   → Team B
        };

        // ================================================================
        // データ構造
        // ================================================================

        private struct HitData
        {
            public Vector3 pos;
            public Vector3 normal;
            public int     colorIndex;
        }

        // スプレー1枚の記録（面積計算に使用）
        private class SprayRecord
        {
            public ulong      SkinID;
            public Vector3    Center;     // XZ 平面で使用
            public float      Radius;
            public int        TeamID;     // 0=A, 1=B
            public BaseEntity Entity;     // Kill() で除去するために保持
        }

        // ================================================================
        // 状態
        // ================================================================

        // 着弾バッファ（プレイヤーごと）
        private readonly Dictionary<ulong, List<HitData>> _hits = new Dictionary<ulong, List<HitData>>();

        // スプレー履歴（先頭が最新）
        private readonly List<SprayRecord> _paintHistory = new List<SprayRecord>();

        // 面積カウント（非同期更新）
        private int[] _areaCount    = new int[2];
        private bool  _calcRunning  = false;

        // ゲーム状態
        private enum GameState { Idle, Countdown, Running, Ended }
        private GameState _gameState      = GameState.Idle;
        private float     _gameTimeLeft   = 0f;
        private int       _countdownLeft  = 0;
        private int       _gameDuration   = 180;
        private Timer     _gameTimer;
        private Timer     _calcTimer;
        private Timer     _countdownTimer;

        // 参加者
        private readonly HashSet<ulong> _participants = new HashSet<ulong>();


        // ================================================================
        // ライフサイクル
        // ================================================================

        void OnServerInitialized()
        {
            foreach (var player in BasePlayer.activePlayerList)
                _hits[player.userID] = new List<HitData>();
        }

        void Unload()
        {
            _gameTimer?.Destroy();
            _calcTimer?.Destroy();
            _countdownTimer?.Destroy();
            CleanupSprays();
            foreach (var player in BasePlayer.activePlayerList)
            {
                CuiHelper.DestroyUi(player, "SplatHud");
                CuiHelper.DestroyUi(player, "SplatResult");
                CuiHelper.DestroyUi(player, "SplatCountdown");
            }
        }

        void OnPlayerConnected(BasePlayer player)
        {
            _hits[player.userID] = new List<HitData>();
            // UIは /splatjoin 後に表示する
        }

        void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            _hits.Remove(player.userID);
            _participants.Remove(player.userID);
            CuiHelper.DestroyUi(player, "SplatHud");
            CuiHelper.DestroyUi(player, "SplatResult");
            CuiHelper.DestroyUi(player, "SplatCountdown");
        }

        // ================================================================
        // 着弾検出
        // ================================================================

        // フック1: エンティティ（建物・床など）への着弾
        void OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            if (info?.Weapon == null || info.InitiatorPlayer == null) return;
            if (!(info.Weapon is PaintballGun)) return;
            if (info.HitNormalWorld.y < FLOOR_THRESHOLD) return; // 水平面のみ

            int colorIndex = info.Weapon.GetCachedItem()?.instanceData?.dataInt ?? 0;
            OnPaintballHit(info.InitiatorPlayer, info.HitPositionWorld, info.HitNormalWorld, colorIndex);
        }

        // フック2: 地面・地形への着弾
        void OnPlayerAttack(BasePlayer attacker, HitInfo info)
        {
            if (attacker == null || info == null) return;
            if (!(attacker.GetHeldEntity() is PaintballGun)) return;
            if (info.HitEntity != null) return;
            if (info.HitPositionWorld == Vector3.zero) return;
            if (info.HitNormalWorld.y < FLOOR_THRESHOLD) return; // 水平面のみ

            int colorIndex = info.Weapon?.GetCachedItem()?.instanceData?.dataInt ?? 0;
            OnPaintballHit(attacker, info.HitPositionWorld, info.HitNormalWorld, colorIndex);
        }

        void OnPaintballHit(BasePlayer player, Vector3 pos, Vector3 normal, int colorIndex)
        {
            if (_gameState == GameState.Countdown || _gameState == GameState.Idle) return;
            if (!_participants.Contains(player.userID)) return;

            if (!_hits.ContainsKey(player.userID))
                _hits[player.userID] = new List<HitData>();

            if (normal == Vector3.zero) normal = Vector3.up;

            var hits = _hits[player.userID];
            hits.Add(new HitData { pos = pos, normal = normal, colorIndex = colorIndex });
            if (hits.Count > MAX_TRACKED_HITS) hits.RemoveAt(0);

            CheckCluster(player, hits, pos, colorIndex);
        }

        void CheckCluster(BasePlayer player, List<HitData> hits, Vector3 recentHit, int colorIndex)
        {
            var nearby = hits.Where(h => Vector3.Distance(h.pos, recentHit) <= CLUSTER_RADIUS).ToList();
            if (nearby.Count < HITS_TO_SPRAY) return;

            var used = nearby.Take(HITS_TO_SPRAY).ToList();

            Vector3 center    = Vector3.zero;
            Vector3 avgNormal = Vector3.zero;
            foreach (var h in used) { center += h.pos; avgNormal += h.normal; }
            center    /= used.Count;
            avgNormal  = (avgNormal / used.Count).normalized;

            foreach (var h in used) hits.Remove(h);

            CreateSpray(player, center, avgNormal, colorIndex);
        }

        // ================================================================
        // スプレー生成
        // ================================================================

        void CreateSpray(BasePlayer player, Vector3 position, Vector3 normal, int colorIndex)
        {
            ulong skinID = _colorSkins.ContainsKey(colorIndex)
                ? _colorSkins[colorIndex]
                : (_colorSkins.ContainsKey(0) ? _colorSkins[0] : 0);

            // SprayCan.cs と同じ回転計算
            Vector3 eyePos  = player.eyes.position;
            Vector3 tangent = (new Plane(normal, position).ClosestPointOnPlane(eyePos) - position).normalized;
            if (tangent.sqrMagnitude < 0.001f)
                tangent = Vector3.Cross(normal, Vector3.right).normalized;
            Quaternion rot = Quaternion.LookRotation(tangent, normal);
            rot *= Quaternion.Euler(0f, 0f, 90f);

            bool  isRare = UnityEngine.Random.value < RARE_CHANCE;
            float scale  = isRare ? SCALE_RARE : SCALE_NORMAL;
            float radius = BASE_DECAL_RADIUS * scale;

            var ent = GameManager.server.CreateEntity(SPRAY_PREFAB, position, rot);
            if (ent == null) return;

            ent.skinID = skinID;
            ent.networkEntityScale = true;
            ent.transform.localScale = Vector3.one * scale;
            // OnDeployed を呼ばない → sprayedByPlayer=0 のまま → MaxSpraysPerPlayer の自動削除を回避
            ent.Spawn();

            // 水色・紫以外の弾はスプレー不発動
            if (!_colorToTeam.TryGetValue(colorIndex, out int teamID)) return;

            // ゲーム中の床スプレーのみ記録
            if (_gameState != GameState.Running) return;

            // 重なっている相手チームの古いデカールを削除（上書き表現）
            for (int i = _paintHistory.Count - 1; i >= 0; i--)
            {
                var old = _paintHistory[i];
                if (old.TeamID == teamID) continue;
                float dx = position.x - old.Center.x;
                float dz = position.z - old.Center.z;
                if (dx * dx + dz * dz < (radius + old.Radius) * (radius + old.Radius))
                {
                    if (old.Entity != null && !old.Entity.IsDestroyed)
                        old.Entity.Kill();
                    _paintHistory.RemoveAt(i);
                }
            }

            var record = new SprayRecord
            {
                SkinID = skinID,
                Center = position,
                Radius = radius,
                TeamID = teamID,
                Entity = ent,
            };

            _paintHistory.Insert(0, record); // 先頭＝最新（重なり判定で優先）

            if (_paintHistory.Count > MAX_PAINT_HISTORY)
            {
                var dropped = _paintHistory[_paintHistory.Count - 1];
                if (dropped.Entity != null && !dropped.Entity.IsDestroyed)
                    dropped.Entity.Kill();
                _paintHistory.RemoveAt(_paintHistory.Count - 1);
            }
        }

        // ================================================================
        // 面積計算（非同期）
        // ================================================================

        void StartAreaCalc()
        {
            if (_calcRunning) return;
            _calcRunning = true;

            var snapshot = new List<SprayRecord>(_paintHistory); // スナップショット
            float step = CALC_STEP;

            Task.Run(() =>
            {
                var counts = new int[2];

                if (snapshot.Count > 0)
                {
                    // ステップ1: バウンディングボックス
                    float minX = float.MaxValue, maxX = float.MinValue;
                    float minZ = float.MaxValue, maxZ = float.MinValue;
                    foreach (var s in snapshot)
                    {
                        if (s.Center.x - s.Radius < minX) minX = s.Center.x - s.Radius;
                        if (s.Center.x + s.Radius > maxX) maxX = s.Center.x + s.Radius;
                        if (s.Center.z - s.Radius < minZ) minZ = s.Center.z - s.Radius;
                        if (s.Center.z + s.Radius > maxZ) maxZ = s.Center.z + s.Radius;
                    }

                    // ステップ2・3: グリッドサンプリング
                    for (float px = minX; px <= maxX; px += step)
                    {
                        for (float pz = minZ; pz <= maxZ; pz += step)
                        {
                            for (int i = 0; i < snapshot.Count; i++)
                            {
                                var s  = snapshot[i];
                                float dx = px - s.Center.x;
                                float dz = pz - s.Center.z;
                                // √ を使わず2乗で比較（パフォーマンス向上）
                                if (dx * dx + dz * dz <= s.Radius * s.Radius)
                                {
                                    counts[s.TeamID]++;
                                    break; // 最新（先頭）にヒットした時点で確定
                                }
                            }
                        }
                    }
                }

                // メインスレッドに結果を返す
                NextTick(() =>
                {
                    _areaCount   = counts;
                    _calcRunning = false;
                    UpdateAllHud();
                });
            });
        }

        // ================================================================
        // ゲーム管理
        // ================================================================

        void StartCountdown(int countdownSeconds, int gameDuration)
        {
            _gameDuration  = gameDuration;
            _gameState     = GameState.Countdown;
            _countdownLeft = countdownSeconds;
            _paintHistory.Clear();
            _areaCount     = new int[2];
            _calcRunning   = false;
            _participants.Clear();

            _gameTimer?.Destroy();
            _calcTimer?.Destroy();
            _countdownTimer?.Destroy();

            _countdownTimer = timer.Every(1f, () =>
            {
                _countdownLeft--;
                if (_countdownLeft <= 0)
                {
                    _countdownTimer?.Destroy();
                    _countdownTimer = null;
                    BroadcastParticipants(p => DrawCountdown(p, 0)); // "GO!"
                    timer.Once(1f, () =>
                    {
                        BroadcastParticipants(p => CuiHelper.DestroyUi(p, "SplatCountdown"));
                        StartGame(_gameDuration);
                    });
                }
                else
                {
                    BroadcastParticipants(p => DrawCountdown(p, _countdownLeft));
                }
            });
        }

        // 参加者全員に処理を実行するヘルパー
        void BroadcastParticipants(System.Action<BasePlayer> action)
        {
            foreach (var p in BasePlayer.activePlayerList)
                if (_participants.Contains(p.userID)) action(p);
        }

        void StartGame(int seconds)
        {
            _gameState         = GameState.Running;
            _gameTimeLeft      = seconds;
            _paintHistory.Clear();
            _areaCount         = new int[2];
            _calcRunning       = false;

            _gameTimer?.Destroy();
            _gameTimer = timer.Every(1f, () =>
            {
                _gameTimeLeft--;
                // タイマーが 0 になった瞬間に記録を締め切り、計算へ
                if (_gameTimeLeft <= 0f)
                {
                    _gameTimeLeft = 0f;
                    EndGame();
                }
                else
                {
                    UpdateAllHud();
                }
            });

            // 一定周期で面積を別スレッド計算
            _calcTimer?.Destroy();
            _calcTimer = timer.Every(CALC_INTERVAL, StartAreaCalc);

            UpdateAllHud();
        }

        void EndGame()
        {
            // まずゲーム状態を Ended に変更 → 以降 CreateSpray で記録されない
            _gameState = GameState.Ended;
            _gameTimer?.Destroy();
            _calcTimer?.Destroy();

            // 前の計算が走っていてもリセットして最終計算を確実に実行
            _calcRunning = false;
            StartAreaCalc();

            // 非同期計算の完了を待つ（十分な余裕: 3秒）
            timer.Once(3f, ShowFinalResult);
        }

        void ShowFinalResult()
        {
            int winTeam = _areaCount[0] > _areaCount[1] ? 0
                        : _areaCount[1] > _areaCount[0] ? 1
                        : -1;

            int   total = _areaCount[0] + _areaCount[1];
            float pctA  = total > 0 ? (float)_areaCount[0] / total * 100f : 0f;
            float pctB  = total > 0 ? (float)_areaCount[1] / total * 100f : 0f;

            string winner = winTeam == -1 ? "DRAW"
                          : winTeam == 0  ? "TEAM A (水色) WIN"
                          :                 "TEAM B (紫) WIN";
            string chatLine = $"<color=#ffff00>■ GAME RESULT ■</color>  {winner}  |  "
                            + $"<color=#2de5ff>Team A {pctA:F1}%</color> vs "
                            + $"<color=#b233ff>Team B {pctB:F1}%</color>";

            // グローバルチャットに結果報告
            foreach (var p in BasePlayer.activePlayerList)
                p.ChatMessage(chatLine);

            // 参加者にリザルト画面
            BroadcastParticipants(p => ShowResult(p, winTeam));

            // リザルト表示終了後にスプレーを全削除
            timer.Once(21f, CleanupSprays);
        }

        void CleanupSprays()
        {
            foreach (var record in _paintHistory)
            {
                if (record.Entity != null && !record.Entity.IsDestroyed)
                    record.Entity.Kill();
            }
            _paintHistory.Clear();
        }

        // ================================================================
        // HUD
        // ================================================================

        void UpdateAllHud()
        {
            BroadcastParticipants(DrawHud);
        }

        void DrawHud(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, "SplatHud");
            if (_gameState == GameState.Idle) return;

            int   total = _areaCount[0] + _areaCount[1];
            float pctA  = total > 0 ? (float)_areaCount[0] / total : 0f;
            float pctB  = total > 0 ? (float)_areaCount[1] / total : 0f;
            float areaA = _areaCount[0] * CALC_STEP * CALC_STEP;
            float areaB = _areaCount[1] * CALC_STEP * CALC_STEP;

            int minutes = (int)_gameTimeLeft / 60;
            int secs    = (int)_gameTimeLeft % 60;
            bool lastTen = _gameTimeLeft <= 10f && _gameState == GameState.Running;

            var c = new CuiElementContainer();

            // ── ルートパネル（画面上部中央）
            c.Add(new CuiPanel
            {
                Image = { Color = "0 0 0 0" },
                RectTransform = { AnchorMin = "0.2 0.88", AnchorMax = "0.8 0.99" },
                CursorEnabled = false
            }, "Hud", "SplatHud");

            // ── タイマー（中央・大きめ）
            c.Add(new CuiLabel
            {
                Text =
                {
                    Text = _gameState == GameState.Ended ? "FINAL" : $"{minutes:00}:{secs:00}",
                    FontSize = 26,
                    Align = TextAnchor.MiddleCenter,
                    Color = lastTen ? "1.0 0.25 0.25 1.0" : "1 1 1 1"
                },
                RectTransform = { AnchorMin = "0.35 0.55", AnchorMax = "0.65 1.0" }
            }, "SplatHud");

            // ── Team A ラベル（左）
            c.Add(new CuiLabel
            {
                Text =
                {
                    Text = $"TEAM A\n{areaA:F1} m²",
                    FontSize = 12,
                    Align = TextAnchor.MiddleLeft,
                    Color = COLOR_A
                },
                RectTransform = { AnchorMin = "0.0 0.55", AnchorMax = "0.33 1.0" }
            }, "SplatHud");

            // ── Team A 割合（左・バー上）
            c.Add(new CuiLabel
            {
                Text =
                {
                    Text = $"{pctA * 100f:F1}%",
                    FontSize = 11,
                    Align = TextAnchor.MiddleLeft,
                    Color = COLOR_A
                },
                RectTransform = { AnchorMin = "0.0 0.35", AnchorMax = "0.35 0.58" }
            }, "SplatHud");

            // ── Team B ラベル（右）
            c.Add(new CuiLabel
            {
                Text =
                {
                    Text = $"TEAM B\n{areaB:F1} m²",
                    FontSize = 12,
                    Align = TextAnchor.MiddleRight,
                    Color = COLOR_B
                },
                RectTransform = { AnchorMin = "0.67 0.55", AnchorMax = "1.0 1.0" }
            }, "SplatHud");

            // ── Team B 割合（右・バー上）
            c.Add(new CuiLabel
            {
                Text =
                {
                    Text = $"{pctB * 100f:F1}%",
                    FontSize = 11,
                    Align = TextAnchor.MiddleRight,
                    Color = COLOR_B
                },
                RectTransform = { AnchorMin = "0.65 0.35", AnchorMax = "1.0 0.58" }
            }, "SplatHud");

            // ── ゲージバー 背景
            c.Add(new CuiPanel
            {
                Image = { Color = "0.08 0.08 0.08 0.85" },
                RectTransform = { AnchorMin = "0.0 0.0", AnchorMax = "1.0 0.34" }
            }, "SplatHud", "SplatHud_Bar");

            // ── ゲージ: Team A（左から pctA まで）
            if (pctA > 0.002f)
            {
                float fillA = Mathf.Clamp(pctA, 0f, 1f);
                c.Add(new CuiPanel
                {
                    Image = { Color = COLOR_A },
                    RectTransform = { AnchorMin = "0.001 0.05", AnchorMax = $"{fillA:F3} 0.95" }
                }, "SplatHud_Bar");
            }

            // ── ゲージ: Team B（pctA から右端まで）
            // pctA + pctB == 1.0 なので境界は pctA
            if (pctB > 0.002f)
            {
                float splitX = Mathf.Clamp(pctA, 0f, 1f);
                c.Add(new CuiPanel
                {
                    Image = { Color = COLOR_B },
                    RectTransform = { AnchorMin = $"{splitX:F3} 0.05", AnchorMax = "0.999 0.95" }
                }, "SplatHud_Bar");
            }

            // ── 中央分割ライン
            if (total > 0)
            {
                float lineX = Mathf.Clamp(pctA - 0.003f, 0f, 1f);
                c.Add(new CuiPanel
                {
                    Image = { Color = "1 1 1 0.9" },
                    RectTransform = { AnchorMin = $"{lineX:F3} 0", AnchorMax = $"{lineX + 0.006f:F3} 1" }
                }, "SplatHud_Bar");
            }

            CuiHelper.AddUi(player, c);
        }

        // ================================================================
        // 結果表示
        // ================================================================

        void ShowResult(BasePlayer player, int winTeam)
        {
            CuiHelper.DestroyUi(player, "SplatResult");

            int   total = _areaCount[0] + _areaCount[1];
            float pctA  = total > 0 ? (float)_areaCount[0] / total * 100f : 0f;
            float pctB  = total > 0 ? (float)_areaCount[1] / total * 100f : 0f;
            float areaA = _areaCount[0] * CALC_STEP * CALC_STEP;
            float areaB = _areaCount[1] * CALC_STEP * CALC_STEP;

            string resultText  = winTeam == -1 ? "DRAW!"
                               : winTeam == 0  ? "TEAM A WIN!"
                               :                 "TEAM B WIN!";
            string resultColor = winTeam == -1 ? "1 1 1 1"
                               : winTeam == 0  ? COLOR_A
                               :                 COLOR_B;

            var c = new CuiElementContainer();

            // 背景
            c.Add(new CuiPanel
            {
                Image = { Color = "0.03 0.03 0.12 0.85" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                CursorEnabled = false
            }, "Overlay", "SplatResult");

            // 結果テキスト（大）
            c.Add(new CuiLabel
            {
                Text = { Text = resultText, FontSize = 72, Align = TextAnchor.MiddleCenter, Color = resultColor },
                RectTransform = { AnchorMin = "0.1 0.60", AnchorMax = "0.9 0.82" }
            }, "SplatResult");

            // Team A 比率（大・左寄り）
            c.Add(new CuiLabel
            {
                Text =
                {
                    Text     = $"TEAM A\n{pctA:F1}%\n{areaA:F1} m²",
                    FontSize = 36,
                    Align    = TextAnchor.MiddleCenter,
                    Color    = COLOR_A
                },
                RectTransform = { AnchorMin = "0.05 0.32", AnchorMax = "0.45 0.60" }
            }, "SplatResult");

            // VS ラベル
            c.Add(new CuiLabel
            {
                Text = { Text = "VS", FontSize = 28, Align = TextAnchor.MiddleCenter, Color = "1 1 1 0.6" },
                RectTransform = { AnchorMin = "0.43 0.38", AnchorMax = "0.57 0.54" }
            }, "SplatResult");

            // Team B 比率（大・右寄り）
            c.Add(new CuiLabel
            {
                Text =
                {
                    Text     = $"TEAM B\n{pctB:F1}%\n{areaB:F1} m²",
                    FontSize = 36,
                    Align    = TextAnchor.MiddleCenter,
                    Color    = COLOR_B
                },
                RectTransform = { AnchorMin = "0.55 0.32", AnchorMax = "0.95 0.60" }
            }, "SplatResult");

            // 比率バー（背景）
            c.Add(new CuiPanel
            {
                Image = { Color = "0.1 0.1 0.1 0.9" },
                RectTransform = { AnchorMin = "0.1 0.26", AnchorMax = "0.9 0.31" }
            }, "SplatResult", "SplatResult_Bar");

            // 比率バー Team A
            if (pctA > 0f)
            {
                float fillA = Mathf.Clamp(pctA / 100f, 0f, 1f);
                c.Add(new CuiPanel
                {
                    Image = { Color = COLOR_A },
                    RectTransform = { AnchorMin = "0 0", AnchorMax = $"{fillA:F3} 1" }
                }, "SplatResult_Bar");
            }
            // 比率バー Team B
            if (pctB > 0f)
            {
                float splitX = Mathf.Clamp(pctA / 100f, 0f, 1f);
                c.Add(new CuiPanel
                {
                    Image = { Color = COLOR_B },
                    RectTransform = { AnchorMin = $"{splitX:F3} 0", AnchorMax = "1 1" }
                }, "SplatResult_Bar");
            }

            CuiHelper.AddUi(player, c);

            // 20秒後に自動クローズ
            timer.Once(20f, () =>
            {
                if (player != null && player.IsConnected)
                {
                    CuiHelper.DestroyUi(player, "SplatResult");
                    CuiHelper.DestroyUi(player, "SplatHud");
                }
                _gameState = GameState.Idle;
            });
        }

        // ================================================================
        // カウントダウン UI
        // ================================================================

        void DrawCountdown(BasePlayer player, int n)
        {
            CuiHelper.DestroyUi(player, "SplatCountdown");

            string text  = n > 0 ? n.ToString() : "GO!";
            string color = n > 0 ? "1.0 0.85 0.1 1.0" : "0.15 0.95 0.15 1.0"; // 黄→緑

            var c = new CuiElementContainer();

            c.Add(new CuiPanel
            {
                Image = { Color = "0 0 0 0.45" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                CursorEnabled = false
            }, "Overlay", "SplatCountdown");

            c.Add(new CuiLabel
            {
                Text =
                {
                    Text     = text,
                    FontSize = 120,
                    Align    = TextAnchor.MiddleCenter,
                    Color    = color
                },
                RectTransform = { AnchorMin = "0.1 0.3", AnchorMax = "0.9 0.7" }
            }, "SplatCountdown");

            CuiHelper.AddUi(player, c);
        }

        // ================================================================
        // コマンド
        // ================================================================

        [ChatCommand("splatjoin")]
        void CmdJoin(BasePlayer player, string command, string[] args)
        {
            if (_gameState == GameState.Idle || _gameState == GameState.Ended)
            {
                player.ChatMessage("[SplatSpray] 現在ゲームは開催されていません。開始を待ってください。");
                return;
            }
            if (_participants.Contains(player.userID))
            {
                player.ChatMessage("[SplatSpray] すでに参加登録済みです。");
                return;
            }

            _participants.Add(player.userID);
            player.ChatMessage($"[SplatSpray] 参加登録しました！ 水色か紫の弾でペイントボールガンを撃とう！");

            // 参加時点の状態に合わせてUIを出す
            if (_gameState == GameState.Countdown)
                DrawCountdown(player, _countdownLeft);
            else if (_gameState == GameState.Running)
                DrawHud(player);
        }

        // 現在持っているペイントボールガンの弾色インデックスを確認する
        [ChatCommand("splatcolorcheck")]
        void CmdColorCheck(BasePlayer player, string command, string[] args)
        {
            var gun = player.GetHeldEntity() as PaintballGun;
            if (gun == null)
            {
                player.ChatMessage("[SplatSpray] ペイントボールガンを手に持った状態で実行してください");
                return;
            }

            var item = gun.GetCachedItem();
            int idx = item?.instanceData?.dataInt ?? -1;

            player.ChatMessage($"[SplatSpray] 現在の弾色インデックス: <color=#2de5ff>{idx}</color>");
            player.ChatMessage("--- 現在の色設定 ---");
            foreach (var kv in _colorToTeam)
            {
                string teamName = kv.Value == 0 ? "<color=#2de5ff>Team A (水色)</color>"
                                                : "<color=#b233ff>Team B (紫)</color>";
                bool hasSkin = _colorSkins.ContainsKey(kv.Key);
                player.ChatMessage($"  index {kv.Key} → {teamName}  スキン:{(hasSkin ? "あり" : "未設定")}");
            }
            player.ChatMessage("(弾の色を変えて再度実行すると別の色のインデックスを確認できます)");
        }

        [ChatCommand("splatstart")]
        void CmdStart(BasePlayer player, string command, string[] args)
        {
            if (_gameState == GameState.Running || _gameState == GameState.Countdown)
            {
                player.ChatMessage("ゲームはすでに進行中です");
                return;
            }
            int countdown = 10;
            int seconds   = 180;
            if (args.Length > 0) int.TryParse(args[0], out countdown);
            if (args.Length > 1) int.TryParse(args[1], out seconds);
            countdown = Mathf.Clamp(countdown, 3, 60);
            seconds   = Mathf.Clamp(seconds,   30, 600);

            foreach (var p in BasePlayer.activePlayerList)
            {
                p.ChatMessage($"<color=#ffff00>■ SplatSpray ゲーム開始まで {countdown} 秒！</color>");
                p.ChatMessage($"制限時間: {seconds} 秒  |  参加するには <color=#00ff88>/splatjoin</color> を入力してください");
                p.ChatMessage("水色か紫のペイントボールで地面を塗り合おう！");
            }

            StartCountdown(countdown, seconds);
        }

        [ChatCommand("splatstop")]
        void CmdStop(BasePlayer player, string command, string[] args)
        {
            if (_gameState == GameState.Countdown)
            {
                _countdownTimer?.Destroy();
                _countdownTimer = null;
                _gameState = GameState.Idle;
                BroadcastParticipants(p =>
                {
                    CuiHelper.DestroyUi(p, "SplatCountdown");
                    p.ChatMessage("[SplatSpray] カウントダウンを中止しました");
                });
                _participants.Clear();
                return;
            }
            if (_gameState != GameState.Running)
            {
                player.ChatMessage("ゲームは進行中ではありません");
                return;
            }
            EndGame();
        }

    }
}
