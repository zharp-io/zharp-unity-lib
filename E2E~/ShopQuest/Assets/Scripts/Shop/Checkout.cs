using System.Collections.Generic;
using UnityEngine;

namespace Shop
{
    public class Item
    {
        public string Name;
    }

    /// <summary>The game code the e2e errors come from: check.py expects these lines.</summary>
    public static class Checkout
    {
        static List<Item> cart;

        public static void Open()
        {
            Debug.LogWarning("Coupon SPRING expired");
            AddToCart(new Item { Name = "sword" });
        }

        static void AddToCart(Item item)
        {
            cart.Add(item); // line 24: cart is null
        }

        public static void Save(int slot)
        {
            Zharp.CaptureError("Save failed: disk full", new Dictionary<string, object> { ["slot"] = slot });
        }
    }
}
