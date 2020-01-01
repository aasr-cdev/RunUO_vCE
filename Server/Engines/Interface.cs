﻿using System;
using System.Collections.Generic;
using Server.Mobiles;

namespace Server
{
    public interface IVendor
    {
        bool OnBuyItems(Mobile from, List<BuyItemResponse> list);
        bool OnSellItems(Mobile from, List<SellItemResponse> list);

        DateTime LastRestock { get; set; }
        TimeSpan RestockDelay { get; }
        void Restock();
    }

    public interface IPoint2D
    {
        int X { get; }
        int Y { get; }
    }

    public interface IPoint3D : IPoint2D
    {
        int Z { get; }
    }

    public interface ICarvable
    {
        void Carve(Mobile from, Item item);
    }

    public interface IWeapon
    {
        int MaxRange { get; }
        void OnBeforeSwing(Mobile attacker, Mobile defender);
        TimeSpan OnSwing(Mobile attacker, Mobile defender);
        void GetStatusDamage(Mobile from, out int min, out int max);
    }

    public interface IHued
    {
        int HuedItemID { get; }
    }

    public interface ISpell
    {
        bool IsCasting { get; }
        void OnCasterHurt();
        void OnCasterKilled();
        void OnConnectionChanged();
        bool OnCasterMoving(Direction d);
        bool OnCasterEquiping(Item item);
        bool OnCasterUsingObject(object o);
        bool OnCastInTown(Region r);
    }

    public interface IParty
    {
        void OnStamChanged(Mobile m);
        void OnManaChanged(Mobile m);
        void OnStatsQuery(Mobile beholder, Mobile beheld);
    }

    public interface ISpawner
    {
        bool UnlinkOnTaming { get; }
        Point3D HomeLocation { get; }
        int HomeRange { get; }

        void Remove(ISpawnable spawn);
    }

    public interface ISpawnable : IEntity
    {
        void OnBeforeSpawn(Point3D location, Map map);
        void MoveToWorld(Point3D location, Map map);
        void OnAfterSpawn();

        ISpawner Spawner { get; set; }
    }
}

namespace Server.Mobiles
{
    public interface IMount
    {
        Mobile Rider { get; set; }
        void OnRiderDamaged(int amount, Mobile from, bool willKill);
    }

    public interface IMountItem
    {
        IMount Mount { get; }
    }
}