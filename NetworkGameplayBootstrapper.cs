using UnityEngine;
using UnityEngine.AI;
using Mirror;
using RPG.Data;
using RPG.UI;
using RPG.Managers;
using RPG.Character;

namespace RPG.Network
{
    /// <summary>
    /// NetworkPlayer — representa um jogador na rede.
    ///
    /// CORREÇÕES v3:
    ///   - Adicionado ServerApplyDamage() [Server] para o mob atacar o player
    ///     sem passar por Command (Commands só cliente→servidor)
    ///   - Adicionado RpcPlayerDied() para mostrar tela de morte no cliente dono
    ///   - Adicionado CmdRequestRespawn() para o cliente pedir respawn
    ///   - Corrigido: CmdSyncHP agora aceita chamada do próprio cliente dono
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent))]
    [RequireComponent(typeof(NetworkTransformReliable))]
    public class NetworkPlayer : NetworkBehaviour
    {
        // ── SyncVars ──────────────────────────────────────────────────────
        [SyncVar(hook = nameof(OnNameChanged))]
        public string CharacterName = "...";

        [SyncVar(hook = nameof(OnRaceChanged))]
        public string Race = "Human";

        [SyncVar]
        public int Level = 1;

        [SyncVar(hook = nameof(OnHPChanged))]
        public float CurrentHP = 100f;

        [SyncVar]
        public float MaxHP = 100f;

        [SyncVar(hook = nameof(OnMovingChanged))]
        public bool IsMoving = false;

        // ── Componentes ───────────────────────────────────────────────────
        private NavMeshAgent _agent;
        private Animator     _animator;

        [Header("Visuals")]
        [SerializeField] private GameObject         localIndicator;
        [SerializeField] private GameObject         nameTagRoot;
        [SerializeField] private TMPro.TMP_Text     nameTagText;
        [SerializeField] private UnityEngine.UI.Slider hpBarSlider;

        [Header("Spawn")]
        [SerializeField] private Transform[] spawnPoints;  // arraste os transforms de spawn no Inspector

        // ── Dados locais (só no dono) ─────────────────────────────────────
        private CharacterData            _charData;
        private RPG.Combat.SkillSystem   _skillSystem;
        private PlayerEntity             _playerEntity;
        private float                    _moveCheckTimer;

        // _isDead local controla bloqueio de input — independente da SyncVar
        private bool _isDead;

        public bool Dead => CurrentHP <= 0f;

        // ── Unity ─────────────────────────────────────────────────────────

        private void Awake()
        {
            _agent        = GetComponent<NavMeshAgent>();
            _animator     = GetComponentInChildren<Animator>();
            _skillSystem  = GetComponent<RPG.Combat.SkillSystem>();
            _playerEntity = GetComponent<PlayerEntity>();
        }

        public override void OnStartClient()
        {
            if (nameTagText != null)
                nameTagText.text = CharacterName;

            if (localIndicator != null)
                localIndicator.SetActive(isLocalPlayer);
        }

        public override void OnStartLocalPlayer()
        {
            Debug.Log($"[NetworkPlayer] Local player: {CharacterName}");

            _charData = GameManager.Instance?.SelectedCharacter;
            if (_charData == null) return;

            CmdSetCharacterInfo(
                _charData.CharacterName,
                _charData.Race.ToString(),
                _charData.Level,
                _charData.CurrentHP,
                _charData.GetDerivedStats().MaxHP
            );

            _playerEntity = GetComponent<PlayerEntity>();
            _playerEntity?.Initialize(_charData);

            var cam = FindObjectOfType<RPG.Systems.CameraController>();
            cam?.SetTarget(transform);

            UIManager.Instance?.BindLocalPlayer(_playerEntity);
        }

        private void Update()
        {
            if (!isLocalPlayer) return;
            if (_isDead) return;   // ← bloqueia todo input quando morto

            _moveCheckTimer += Time.deltaTime;
            if (_moveCheckTimer >= 0.1f)
            {
                _moveCheckTimer = 0f;
                bool moving = _agent.velocity.sqrMagnitude > 0.05f;
                if (moving != IsMoving)
                    CmdSetMoving(moving);
            }

            UpdateAnimations(_agent.velocity.sqrMagnitude > 0.05f);
        }

        // ── Commands (Cliente → Servidor) ─────────────────────────────────

        [Command]
        private void CmdSetCharacterInfo(string charName, string race, int level, float hp, float maxHp)
        {
            CharacterName = charName;
            Race          = race;
            Level         = level;
            CurrentHP     = hp;
            MaxHP         = maxHp;
        }

        [Command]
        public void CmdMoveTo(Vector3 destination)
        {
            if (NavMesh.SamplePosition(destination, out NavMeshHit hit, 2f, NavMesh.AllAreas))
                _agent.SetDestination(hit.position);
        }

        [Command]
        public void CmdSetMoving(bool moving)
        {
            IsMoving = moving;
        }

        [Command]
        public void CmdSyncHP(float hp, float maxHp)
        {
            CurrentHP = hp;
            MaxHP     = maxHp;
        }

        /// <summary>
        /// Cliente pede respawn ao servidor.
        /// </summary>
        [Command]
        public void CmdRequestRespawn()
        {
            ServerRespawn();
        }

        // ── Server Methods (Servidor → Servidor) ──────────────────────────

        /// <summary>
        /// BUG CORRIGIDO: método [Server] para mobs atacarem o player.
        /// Antes era CmdTakeDamage() que só funciona quando chamado por CLIENTE.
        /// O servidor chamando um Command resulta em erro silencioso.
        /// </summary>
        [Server]
        public void ServerApplyDamage(float dmg)
        {
            if (Dead) return;

            CurrentHP = Mathf.Max(0f, CurrentHP - dmg);

            if (CurrentHP <= 0f)
                ServerDie();
        }

        [Server]
        private void ServerDie()
        {
            CurrentHP = 0f;
            if (_agent != null) _agent.ResetPath();

            // Avisa o cliente dono para mostrar a tela de morte
            RpcPlayerDied();
            Debug.Log($"[NetworkPlayer] {CharacterName} morreu no servidor.");
        }

        [Server]
        private void ServerRespawn()
        {
            Vector3 spawnPos = GetSpawnPosition();
            transform.position = spawnPos;

            CurrentHP = MaxHP * 0.5f;

            RpcOnRespawned(spawnPos, CurrentHP, MaxHP);
            Debug.Log($"[NetworkPlayer] {CharacterName} respawnou em {spawnPos}.");
        }

        [Server]
        private Vector3 GetSpawnPosition()
        {
            if (spawnPoints != null && spawnPoints.Length > 0)
            {
                int idx = Random.Range(0, spawnPoints.Length);
                return spawnPoints[idx].position;
            }
            // Fallback: posição inicial
            return Vector3.zero;
        }

        // ── ClientRpcs (Servidor → Clientes) ─────────────────────────────

        [ClientRpc]
        private void RpcPlayerDied()
        {
            // Só o cliente dono processa morte local
            if (!isLocalPlayer) return;

            _isDead = true;

            // Para o NavMeshAgent imediatamente
            if (_agent != null)
            {
                _agent.ResetPath();
                _agent.isStopped = true;
            }

            // Desativa os controllers de input para bloquear movimento e skills
            var playerCtrl  = GetComponent<RPG.Systems.PlayerController>();
            var networkCtrl = GetComponent<NetworkPlayerController>();
            if (playerCtrl  != null) playerCtrl.enabled  = false;
            if (networkCtrl != null) networkCtrl.enabled = false;

            // Propaga HP=0 ao PlayerEntity para o UIManager atualizar a barra
            _playerEntity?.OnNetworkDeath();

            DeathScreenUI.Show(this);
            Debug.Log("[NetworkPlayer] Tela de morte exibida.");
        }

        [ClientRpc]
        private void RpcOnRespawned(Vector3 position, float hp, float maxHp)
        {
            if (!isLocalPlayer) return;

            _isDead = false;

            // Reativa NavMeshAgent
            if (_agent != null)
            {
                _agent.isStopped = false;
                _agent.Warp(position);  // teleporta sem interpolar
            }

            // Reativa controllers de input
            var playerCtrl  = GetComponent<RPG.Systems.PlayerController>();
            var networkCtrl = GetComponent<NetworkPlayerController>();
            if (playerCtrl  != null) playerCtrl.enabled  = true;
            if (networkCtrl != null) networkCtrl.enabled = true;

            // Atualiza HP no PlayerEntity (dispara OnHPChanged → UIManager)
            if (_playerEntity != null)
            {
                _playerEntity.ForceSetHP(hp, maxHp);
                _playerEntity.Respawn(position);
            }

            DeathScreenUI.Hide();
            Debug.Log("[NetworkPlayer] Respawn concluído.");
        }

        [ClientRpc]
        public void RpcPlayAnimation(string trigger)
        {
            _animator?.SetTrigger(trigger);
        }

        // ── SyncVar Hooks ─────────────────────────────────────────────────

        private void OnNameChanged(string oldName, string newName)
        {
            if (nameTagText != null) nameTagText.text = newName;
        }

        private void OnRaceChanged(string oldRace, string newRace) { }

        private void OnHPChanged(float oldHP, float newHP)
        {
            // Atualiza a mini barra de HP acima da cabeça (visível para outros jogadores)
            if (hpBarSlider != null)
            {
                hpBarSlider.maxValue = MaxHP;
                hpBarSlider.value    = newHP;
                hpBarSlider.gameObject.SetActive(newHP < MaxHP);
            }

            // ─── CORREÇÃO PRINCIPAL ───────────────────────────────────────
            // Propaga HP para o PlayerEntity do cliente LOCAL, que dispara
            // OnHPChanged → UIManager atualiza a barra de HP do HUD.
            // Sem isso, o servidor atualiza CurrentHP via SyncVar mas o HUD
            // nunca sabe que o HP mudou.
            if (isLocalPlayer && _playerEntity != null && _playerEntity.IsInitialized)
                _playerEntity.ForceSetHP(newHP, MaxHP);
        }

        private void OnMovingChanged(bool oldVal, bool newVal)
        {
            if (!isLocalPlayer) UpdateAnimations(newVal);
        }

        // ── Animações ─────────────────────────────────────────────────────

        private void UpdateAnimations(bool moving)
        {
            if (_animator == null) return;
            _animator.SetBool("IsMoving", moving);
        }

        // ── ITargetable ───────────────────────────────────────────────────

        public string DisplayName => CharacterName;
        public float  HPCurrent   => CurrentHP;
        public float  HPMax       => MaxHP;
    }
}
