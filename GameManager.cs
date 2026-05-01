# GUIA DE CONFIGURAÇÃO — MULTIPLAYER COM MIRROR

## 1. INSTALAR O MIRROR

Window → Package Manager → "+" → Add package by git URL:
```
https://github.com/MirrorNetworking/Mirror.git#v89.0.0
```
Aguarde importar. Ele instala também: kcp2k (transport padrão).

---

## 2. CONFIGURAR A GAMEPLAY SCENE

### A) NetworkManager GameObject
Crie um Empty chamado `NetworkManager` e adicione:
- `RPGNetworkManager` (nosso script — substitui o padrão)
- `KcpTransport` (add component, vem com o Mirror)

No `RPGNetworkManager` Inspector:
- **Player Prefab** → arraste o `NetworkPlayerPrefab`
- **Spawn Points** → arraste os Transforms de spawn
- **Network Address** → `localhost` (para testes)
- **Max Connections** → 100

### B) NetworkGameplayBootstrapper GameObject
Crie um Empty chamado `NetworkBootstrapper`:
- Adicione `NetworkGameplayBootstrapper`
- **Start As Host** → ✅ marcado no PRIMEIRO cliente de teste
- Nos demais: desmarcado

### C) NetworkUIConnector
No mesmo objeto do `UIManager`, adicione `NetworkUIConnector`

---

## 3. CRIAR O NetworkPlayer PREFAB

Crie um prefab chamado `NetworkPlayerPrefab` com:

```
NetworkPlayerPrefab (root)
├── NetworkIdentity          ← obrigatório Mirror
├── NetworkTransformReliable ← sincroniza posição
├── NetworkPlayer            ← nosso script
├── NetworkPlayerController  ← input local
├── NavMeshAgent
├── PlayerEntity             ← stats/dano
├── SkillSystem              ← skills
├── CapsuleCollider          ← layer: Targetable
│
├── [Modelo 3D filho]
│   └── Animator
│
└── [WorldSpaceCanvas filho] ← Billboard
    ├── NameTagText (TMP)    → campo nameTagText
    ├── HPBar (Slider)       → campo hpBarSlider
    └── LocalIndicator       → campo localIndicator (seta/coroa)
```

**ATENÇÃO**: O prefab DEVE estar em `Assets/Resources/` OU registrado
no campo `Registered Spawnable Prefabs` do RPGNetworkManager.

---

## 4. CRIAR O NetworkMonster PREFAB

```
NetworkMonsterPrefab (root)
├── NetworkIdentity
├── NetworkTransformReliable
├── NetworkMonsterEntity     ← nosso script
├── NavMeshAgent
├── CapsuleCollider          ← layer: Targetable
├── [Modelo 3D filho]
│   └── Animator
└── [WorldSpaceCanvas filho]
    └── MonsterHealthBarUI
```

Registre em `Registered Spawnable Prefabs` do RPGNetworkManager.

---

## 5. SPAWNAR MONSTROS NO SERVIDOR

Crie um `MonsterSpawner` na cena:

```csharp
using Mirror;
using UnityEngine;

public class MonsterSpawner : NetworkBehaviour
{
    [SerializeField] private GameObject monsterPrefab;
    [SerializeField] private Transform[] spawnPoints;

    public override void OnStartServer()
    {
        foreach (var sp in spawnPoints)
        {
            var mob = Instantiate(monsterPrefab, sp.position, sp.rotation);
            NetworkServer.Spawn(mob);
        }
    }
}
```

---

## 6. FLUXO COMPLETO ONLINE

```
Player A abre o jogo
  → Login → Seleciona personagem → GameplayScene
  → NetworkGameplayBootstrapper: StartHost()
  → RPGNetworkManager.OnServerAddPlayer() spawna NetworkPlayerPrefab
  → NetworkPlayer.OnStartLocalPlayer() → CmdSetCharacterInfo()
  → SyncVars propagam nome/raça/HP para todos

Player B abre o jogo
  → Login → Seleciona personagem → GameplayScene  
  → NetworkGameplayBootstrapper: StartClient()
  → Conecta ao servidor do Player A
  → Mirror automaticamente spawna o objeto do Player A na tela do B
  → Mirror spawna o objeto do Player B na tela do A
  → CmdSetCharacterInfo() propaga dados do B para A (e vice-versa)

Player A vê Player B se movendo
  → Player B clica no terreno
  → NetworkPlayerController.HandleMouseInput() → agent.SetDestination() local
  → CmdMoveTo() → servidor executa → NetworkTransformReliable sincroniza posição
  → Player A vê o Player B se mover em tempo real

Monstro toma dano
  → Player A seleciona mob → usa skill Q
  → SkillSystem → NetworkMonsterEntity.TakeDamage() → [Server]
  → _currentHP SyncVar atualiza → todos veem a barra de HP baixar
  → Mob morre → RpcGrantExp() → todos perto ganham XP
```

---

## 7. TESTAR LOCAL (2 instâncias)

1. File → Build Settings → Build
2. Abra o executável (Player A) → marque "Start As Host" → entre no jogo
3. Abra outra instância (Player B, no Editor ou outro executável)
   → desmarque "Start As Host" → IP: `localhost` → entre no jogo
4. Os dois devem se ver no mundo!

---

## 8. SERVIDOR DEDICADO (Produção)

Para um servidor dedicado sem gráficos:
1. Build Settings → marque "Server Build"  
2. No servidor: `NetworkGameplayBootstrapper.startAsHost = true`
3. Jogadores: `startAsHost = false`, IP do servidor

Recomendado: hospedar em VPS Linux (Ubuntu 22.04)
Porta padrão KCP: **7777 UDP** — abra no firewall!
