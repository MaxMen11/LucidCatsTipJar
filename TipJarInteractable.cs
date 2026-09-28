using System;
using Game.Interaction;
using Game.Player;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

namespace LucidCats.TipJar
{
	internal class TipJarInteractable : BaseInteractable
	{
		private const float GlassRadius = 0.06f;
		private const float GlassHeight = 0.12f;
		private const float LidRadius = 0.062f;
		private const float LidThickness = 0.008f;
		private const float CoinsRadius = 0.058f;
		private const float MaxCoinsHeight = GlassHeight * 0.9f;

		private const string MsgDeposit = "LucidCats.TipJar.Deposit";
		private const string MsgWithdraw = "LucidCats.TipJar.Withdraw";

		private static readonly Color GlassColor = new Color(0.3f, 0.4f, 0.6f, 0.3f);
		private static readonly Color LidColor = new Color(0.2f, 0.3f, 0.5f);
		private static readonly Color CoinsColor = new Color(0.7f, 0.7f, 0.2f);

		private static Shader litShader;

		public readonly NetworkVariable<int> Balance = new NetworkVariable<int>(0);

		public readonly NetworkVariable<int> Amount = new NetworkVariable<int>(Plugin.TransferAmount.Value);
		public readonly NetworkVariable<float> Fee = new NetworkVariable<float>(Plugin.DepositFee.Value);

		public static event Action<TipJarInteractable> OnAnySpawned;

		private Transform coins;
		private Renderer coinsRenderer;
		private Collider jarCollider;
		private Renderer[] visualRenderers;
		private bool hidden;

		public static GameObject BuildPrefab()
		{
			var root = new GameObject("LucidCats_TipJar");
			root.layer = LayerMask.NameToLayer("Interactable");
			root.AddComponent<NetworkObject>();

			var collider = root.AddComponent<CapsuleCollider>();
			collider.height = GlassHeight;
			collider.radius = GlassRadius;
			collider.center = new Vector3(0f, GlassHeight * 0.5f, 0f);

			AddCylinder(root.transform, "Glass", GlassRadius, GlassHeight, 0f, GlassColor, true);
			AddCylinder(root.transform, "Lid", LidRadius, LidThickness, GlassHeight, LidColor);
			AddCylinder(root.transform, "Coins", CoinsRadius, 0f, 0f, CoinsColor);

			root.AddComponent<TipJarInteractable>();

			return root;
		}

		protected override void OnAwake()
		{
			coins = transform.Find("Coins");
			coinsRenderer = coins.GetComponent<Renderer>();
			jarCollider = GetComponent<Collider>();
			visualRenderers = GetComponentsInChildren<Renderer>();
		}

		private static Transform AddCylinder(Transform parent, string name, float radius, float height, float bottomY, Color color, bool transparent = false)
		{
			GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
			go.name = name;
			Destroy(go.GetComponent<Collider>());
			go.transform.SetParent(parent, false);
			go.transform.localScale = new Vector3(radius * 2f, height * 0.5f, radius * 2f);
			go.transform.localPosition = new Vector3(0f, bottomY + height * 0.5f, 0f);

			if (litShader == null)
				litShader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");

			var mat = new Material(litShader) { color = color };

			if (transparent && mat.HasProperty("_Surface"))
			{
				mat.SetFloat("_Surface", 1f);
				mat.SetOverrideTag("RenderType", "Transparent");
				mat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
				mat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
				mat.SetInt("_ZWrite", 0);
				mat.renderQueue = (int)RenderQueue.Transparent;
				mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
			}

			go.GetComponent<Renderer>().material = mat;
			return go.transform;
		}

		public override void OnNetworkSpawn()
		{
			base.OnNetworkSpawn();

			if (IsServer)
			{
				SetInteractHoldDuration(0f);

				NetworkManager.CustomMessagingManager.RegisterNamedMessageHandler(
					MsgDeposit, (sender, reader) => ServerTransfer(sender, true));

				NetworkManager.CustomMessagingManager.RegisterNamedMessageHandler(
					MsgWithdraw, (sender, reader) => ServerTransfer(sender, false));
			}

			Balance.OnValueChanged += HandleBalanceChanged;
			HandleBalanceChanged(0, Balance.Value);

			OnAnySpawned?.Invoke(this);
		}

		public override void OnNetworkDespawn()
		{
			if (IsServer)
			{
				NetworkManager.CustomMessagingManager.UnregisterNamedMessageHandler(MsgDeposit);
				NetworkManager.CustomMessagingManager.UnregisterNamedMessageHandler(MsgWithdraw);
			}

			Balance.OnValueChanged -= HandleBalanceChanged;
			base.OnNetworkDespawn();
		}

		protected override string GetIdleText(PlayerManager player)
		{
			int amount = Mathf.Max(1, Amount.Value);
			float fee = Mathf.Max(0f, Fee.Value);

			string deposit = fee > 0f ? $"{amount} @{Mathf.RoundToInt(fee * 100f)}%" : $"{amount}";
			return $"Tip {deposit}\n[RMB/{Plugin.ParsedWithdrawKey}] Take {amount}";
		}

		protected override void OnInteract(PlayerManager player)
		{
			RequestTransfer(player, true);
		}

		private void Update()
		{
			PlayerManager lp = LocalPlayerRegistry.HasInstance ? LocalPlayerRegistry.Instance.Current : null;

			bool dreaming = lp != null && lp.SleepManager != null && lp.SleepManager.IsDreaming;
			if (dreaming != hidden)
			{
				hidden = dreaming;
				jarCollider.enabled = !dreaming;

				foreach (Renderer r in visualRenderers)
					r.enabled = !dreaming;

				coinsRenderer.enabled = !dreaming && Balance.Value > 0;
			}

			if (lp == null || lp.InteractionManager == null || lp.InteractionManager.Hovered != this)
				return;

			Mouse mouse = Mouse.current;
			Keyboard keyboard = Keyboard.current;
			Gamepad gamepad = Gamepad.current;

			bool withdraw = (mouse != null && mouse.rightButton.wasPressedThisFrame)
				|| (keyboard != null && keyboard[Plugin.ParsedWithdrawKey].wasPressedThisFrame)
				|| (gamepad != null && gamepad.buttonEast.wasPressedThisFrame);

			if (withdraw)
				RequestTransfer(lp, false);
		}

		private void RequestTransfer(PlayerManager player, bool deposit)
		{
			if (IsServer)
			{
				ServerApplyTransfer(player, deposit);
				return;
			}

			using var writer = new FastBufferWriter(0, Allocator.Temp);

			NetworkManager.CustomMessagingManager.SendNamedMessage(
				deposit ? MsgDeposit : MsgWithdraw,
				NetworkManager.ServerClientId,
				writer, NetworkDelivery.Reliable);
		}

		private void ServerTransfer(ulong requesterId, bool deposit)
		{
			NetworkObject playerObj =
				NetworkManager.SpawnManager.GetPlayerNetworkObject(requesterId);

			PlayerManager player =
				playerObj != null ? playerObj.GetComponent<PlayerManager>() : null;

			if (player != null)
				ServerApplyTransfer(player, deposit);
		}

		private void ServerApplyTransfer(PlayerManager player, bool deposit)
		{
			if (player == null || player.Valuables == null)
				return;

			int amount = Mathf.Max(1, Amount.Value);
			int capacity = Mathf.Max(1, Plugin.Capacity.Value);
			float fee = Mathf.Max(0f, Fee.Value);

			if (deposit)
			{
				int cost = amount + Mathf.RoundToInt(amount * fee);

				if (player.Valuables.Credits.Value < cost)
					return;

				if (Balance.Value + amount > capacity)
					return;

				player.Valuables.Credits.Value -= cost;
				Balance.Value += amount;
			}
			else
			{
				if (Balance.Value < amount)
					return;

				Balance.Value -= amount;
				player.Valuables.Credits.Value += amount;
			}
		}

		private void HandleBalanceChanged(int previous, int balance)
		{
			coinsRenderer.enabled = balance > 0 && !hidden;

			if (balance <= 0)
				return;

			float unit = Mathf.Max(1, Amount.Value);
			float capacity = Mathf.Max(unit * 10f, Plugin.Capacity.Value);

			// SetFill: 0.1 after the first deposit, 1.0 at capacity,
			// logarithmic in between so the pile rises fast early on.
			// but also cleaner for smaller or inbetween than classic logaritmic
			float fill = 0.1f * Mathf.Pow(balance / unit, 1f / Mathf.Log10(capacity / unit));

			float height = Mathf.Clamp01(fill) * MaxCoinsHeight;
			coins.localScale = new Vector3(CoinsRadius * 2f, height * 0.5f, CoinsRadius * 2f);
			coins.localPosition = new Vector3(0f, height * 0.5f, 0f);
		}

		protected override void __initializeVariables()
		{
			Balance.Initialize(this);
			NetworkVariableFields.Add(Balance);
			Amount.Initialize(this);
			NetworkVariableFields.Add(Amount);
			Fee.Initialize(this);
			NetworkVariableFields.Add(Fee);
			base.__initializeVariables();
		}
	}
}