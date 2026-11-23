using System.Collections.Generic;
using UnityEngine;

namespace Shop
{
    public class Item
    {
        public string Name;
    }

    /// <summary>The game code the e2e errors come from: check.py expects these lines (not inlined: AddToCart stays a frame).</summary>
    public static class Checkout
    {
        static List<Item> cart;

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)] public static void Open()
        {
            Debug.LogWarning("Coupon SPRING expired");
            AddToCart(new Item { Name = "sword" });
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)] static void AddToCart(Item item)
        {
            cart.Add(item); // line 24: cart is null
        }

        public static void Save(int slot)
        {
            Zharp.CaptureError("Save failed: disk full", new Dictionary<string, object> { ["slot"] = slot });
        }

        public static void Pay(int coins)
        {
            try
            {
                Charge(coins);
            }
            catch (System.ArgumentException e)
            {
                Zharp.CaptureException(e, new Dictionary<string, object> { ["coins"] = coins });
            }
        }

        static void Charge(int coins)
        {
            if (coins <= 0) throw new System.ArgumentOutOfRangeException(nameof(coins), "Nothing to pay");
        }
    }
}
