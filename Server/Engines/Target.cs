using System;
using Server;
using Server.Network;

namespace Server.Targeting
{
    public abstract class Target
    {
        private static int m_NextTargetID;

        private static bool m_TargetIDValidation = true;

        public static bool TargetIDValidation
        {
            get { return m_TargetIDValidation; }
            set { m_TargetIDValidation = value; }
        }

        private int m_TargetID;
        private int m_Range;
        private bool m_AllowGround;
        private bool m_CheckLOS;
        private bool m_AllowNonlocal;
        private bool m_DisallowMultis;
        private TargetFlags m_Flags;
        private DateTime m_TimeoutTime;

        public DateTime TimeoutTime { get { return m_TimeoutTime; } }

        protected Target(int range, bool allowGround, TargetFlags flags)
        {
            m_TargetID = ++m_NextTargetID;
            m_Range = range;
            m_AllowGround = allowGround;
            m_Flags = flags;

            m_CheckLOS = true;
        }

        public static void Cancel(Mobile m)
        {
            NetState ns = m.NetState;

            if (ns != null)
                ns.Send(CancelTarget.Instance);

            Target targ = m.Target;

            if (targ != null)
                targ.OnTargetCancel(m, TargetCancelType.Canceled);
        }

        private Timer m_TimeoutTimer;

        public void BeginTimeout(Mobile from, TimeSpan delay)
        {
            m_TimeoutTime = DateTime.UtcNow + delay;

            if (m_TimeoutTimer != null)
                m_TimeoutTimer.Stop();

            m_TimeoutTimer = new TimeoutTimer(this, from, delay);
            m_TimeoutTimer.Start();
        }

        public void CancelTimeout()
        {
            if (m_TimeoutTimer != null)
                m_TimeoutTimer.Stop();

            m_TimeoutTimer = null;
        }

        public void Timeout(Mobile from)
        {
            CancelTimeout();
            from.ClearTarget();

            Cancel(from);

            OnTargetCancel(from, TargetCancelType.Timeout);
            OnTargetFinish(from);
        }

        private class TimeoutTimer : Timer
        {
            private Target m_Target;
            private Mobile m_Mobile;

            private static TimeSpan ThirtySeconds = TimeSpan.FromSeconds(30.0);
            private static TimeSpan TenSeconds = TimeSpan.FromSeconds(10.0);
            private static TimeSpan OneSecond = TimeSpan.FromSeconds(1.0);

            public TimeoutTimer(Target target, Mobile m, TimeSpan delay) : base(delay)
            {
                m_Target = target;
                m_Mobile = m;

                if (delay >= ThirtySeconds)
                    Priority = TimerPriority.FiveSeconds;
                else if (delay >= TenSeconds)
                    Priority = TimerPriority.OneSecond;
                else if (delay >= OneSecond)
                    Priority = TimerPriority.TwoFiftyMS;
                else
                    Priority = TimerPriority.TwentyFiveMS;
            }

            protected override void OnTick()
            {
                if (m_Mobile.Target == m_Target)
                    m_Target.Timeout(m_Mobile);
            }
        }

        public bool CheckLOS
        {
            get
            {
                return m_CheckLOS;
            }
            set
            {
                m_CheckLOS = value;
            }
        }

        public bool DisallowMultis
        {
            get
            {
                return m_DisallowMultis;
            }
            set
            {
                m_DisallowMultis = value;
            }
        }

        public bool AllowNonlocal
        {
            get
            {
                return m_AllowNonlocal;
            }
            set
            {
                m_AllowNonlocal = value;
            }
        }

        public int TargetID
        {
            get
            {
                return m_TargetID;
            }
        }

        public virtual Packet GetPacketFor(NetState ns)
        {
            return new TargetReq(this);
        }

        public void Cancel(Mobile from, TargetCancelType type)
        {
            CancelTimeout();
            from.ClearTarget();

            OnTargetCancel(from, type);
            OnTargetFinish(from);
        }

        public void Invoke(Mobile from, object targeted)
        {
            CancelTimeout();
            from.ClearTarget();

            if (from.Deleted)
            {
                OnTargetCancel(from, TargetCancelType.Canceled);
                OnTargetFinish(from);
                return;
            }

            Point3D loc;
            Map map;

            if (targeted is LandTarget)
            {
                loc = ((LandTarget)targeted).Location;
                map = from.Map;
            }
            else if (targeted is StaticTarget)
            {
                loc = ((StaticTarget)targeted).Location;
                map = from.Map;
            }
            else if (targeted is Mobile)
            {
                if (((Mobile)targeted).Deleted)
                {
                    OnTargetDeleted(from, targeted);
                    OnTargetFinish(from);
                    return;
                }
                else if (!((Mobile)targeted).CanTarget)
                {
                    OnTargetUntargetable(from, targeted);
                    OnTargetFinish(from);
                    return;
                }

                loc = ((Mobile)targeted).Location;
                map = ((Mobile)targeted).Map;
            }
            else if (targeted is Item)
            {
                Item item = (Item)targeted;

                if (item.Deleted)
                {
                    OnTargetDeleted(from, targeted);
                    OnTargetFinish(from);
                    return;
                }
                else if (!item.CanTarget)
                {
                    OnTargetUntargetable(from, targeted);
                    OnTargetFinish(from);
                    return;
                }

                object root = item.RootParent;

                if (!m_AllowNonlocal && root is Mobile && root != from && from.AccessLevel == AccessLevel.Player)
                {
                    OnNonlocalTarget(from, targeted);
                    OnTargetFinish(from);
                    return;
                }

                loc = item.GetWorldLocation();
                map = item.Map;
            }
            else
            {
                OnTargetCancel(from, TargetCancelType.Canceled);
                OnTargetFinish(from);
                return;
            }

            if (map == null || map != from.Map || (m_Range != -1 && !from.InRange(loc, m_Range)))
            {
                OnTargetOutOfRange(from, targeted);
            }
            else
            {
                if (!from.CanSee(targeted))
                    OnCantSeeTarget(from, targeted);
                else if (m_CheckLOS && !from.InLOS(targeted))
                    OnTargetOutOfLOS(from, targeted);
                else if (targeted is Item && ((Item)targeted).InSecureTrade)
                    OnTargetInSecureTrade(from, targeted);
                else if (targeted is Item && !((Item)targeted).IsAccessibleTo(from))
                    OnTargetNotAccessible(from, targeted);
                else if (targeted is Item && !((Item)targeted).CheckTarget(from, this, targeted))
                    OnTargetUntargetable(from, targeted);
                else if (targeted is Mobile && !((Mobile)targeted).CheckTarget(from, this, targeted))
                    OnTargetUntargetable(from, targeted);
                else if (from.Region.OnTarget(from, this, targeted))
                    OnTarget(from, targeted);
            }

            OnTargetFinish(from);
        }

        protected virtual void OnTarget(Mobile from, object targeted)
        {
        }

        protected virtual void OnTargetNotAccessible(Mobile from, object targeted)
        {
            from.SendLocalizedMessage(500447); // That is not accessible.
        }

        protected virtual void OnTargetInSecureTrade(Mobile from, object targeted)
        {
            from.SendLocalizedMessage(500447); // That is not accessible.
        }

        protected virtual void OnNonlocalTarget(Mobile from, object targeted)
        {
            from.SendLocalizedMessage(500447); // That is not accessible.
        }

        protected virtual void OnCantSeeTarget(Mobile from, object targeted)
        {
            from.SendLocalizedMessage(500237); // Target can not be seen.
        }

        protected virtual void OnTargetOutOfLOS(Mobile from, object targeted)
        {
            from.SendLocalizedMessage(500237); // Target can not be seen.
        }

        protected virtual void OnTargetOutOfRange(Mobile from, object targeted)
        {
            from.SendLocalizedMessage(500446); // That is too far away.
        }

        protected virtual void OnTargetDeleted(Mobile from, object targeted)
        {
        }

        protected virtual void OnTargetUntargetable(Mobile from, object targeted)
        {
            from.SendLocalizedMessage(500447); // That is not accessible.
        }

        protected virtual void OnTargetCancel(Mobile from, TargetCancelType cancelType)
        {
        }

        protected virtual void OnTargetFinish(Mobile from)
        {
        }

        public int Range
        {
            get
            {
                return m_Range;
            }
            set
            {
                m_Range = value;
            }
        }

        public bool AllowGround
        {
            get
            {
                return m_AllowGround;
            }
            set
            {
                m_AllowGround = value;
            }
        }

        public TargetFlags Flags
        {
            get
            {
                return m_Flags;
            }
            set
            {
                m_Flags = value;
            }
        }
    }

    public enum TargetFlags : byte
    {
        None = 0x00,
        Harmful = 0x01,
        Beneficial = 0x02,
    }

    public enum TargetCancelType
    {
        Overriden,
        Canceled,
        Disconnected,
        Timeout
    }

    public abstract class MultiTarget : Target
    {
        private int m_MultiID;
        private Point3D m_Offset;

        public int MultiID
        {
            get
            {
                return m_MultiID;
            }
            set
            {
                m_MultiID = value;
            }
        }

        public Point3D Offset
        {
            get
            {
                return m_Offset;
            }
            set
            {
                m_Offset = value;
            }
        }

        protected MultiTarget(int multiID, Point3D offset)
            : this(multiID, offset, 10, true, TargetFlags.None)
        {
        }

        protected MultiTarget(int multiID, Point3D offset, int range, bool allowGround, TargetFlags flags)
            : base(range, allowGround, flags)
        {
            m_MultiID = multiID;
            m_Offset = offset;
        }

        public override Packet GetPacketFor(NetState ns)
        {
            if (ns.HighSeas)
                return new MultiTargetReqHS(this);
            else
                return new MultiTargetReq(this);
        }
    }

    public class LandTarget : IPoint3D
    {
        private Point3D m_Location;
        private int m_TileID;

        public LandTarget(Point3D location, Map map)
        {
            m_Location = location;

            if (map != null)
            {
                m_Location.Z = map.GetAverageZ(m_Location.X, m_Location.Y);
                m_TileID = map.Tiles.GetLandTile(m_Location.X, m_Location.Y).ID & TileData.MaxLandValue;
            }
        }

        [CommandProperty(AccessLevel.Counselor)]
        public string Name
        {
            get
            {
                return TileData.LandTable[m_TileID].Name;
            }
        }

        [CommandProperty(AccessLevel.Counselor)]
        public TileFlag Flags
        {
            get
            {
                return TileData.LandTable[m_TileID].Flags;
            }
        }

        [CommandProperty(AccessLevel.Counselor)]
        public int TileID
        {
            get
            {
                return m_TileID;
            }
        }

        [CommandProperty(AccessLevel.Counselor)]
        public Point3D Location
        {
            get
            {
                return m_Location;
            }
        }

        [CommandProperty(AccessLevel.Counselor)]
        public int X
        {
            get
            {
                return m_Location.X;
            }
        }

        [CommandProperty(AccessLevel.Counselor)]
        public int Y
        {
            get
            {
                return m_Location.Y;
            }
        }

        [CommandProperty(AccessLevel.Counselor)]
        public int Z
        {
            get
            {
                return m_Location.Z;
            }
        }
    }

    public class StaticTarget : IPoint3D
    {
        private Point3D m_Location;
        private int m_ItemID;

        public StaticTarget(Point3D location, int itemID)
        {
            m_Location = location;
            m_ItemID = itemID & TileData.MaxItemValue;
            m_Location.Z += TileData.ItemTable[m_ItemID].CalcHeight;
        }

        [CommandProperty(AccessLevel.Counselor)]
        public Point3D Location
        {
            get
            {
                return m_Location;
            }
        }

        [CommandProperty(AccessLevel.Counselor)]
        public string Name
        {
            get
            {
                return TileData.ItemTable[m_ItemID].Name;
            }
        }

        [CommandProperty(AccessLevel.Counselor)]
        public TileFlag Flags
        {
            get
            {
                return TileData.ItemTable[m_ItemID].Flags;
            }
        }

        [CommandProperty(AccessLevel.Counselor)]
        public int X
        {
            get
            {
                return m_Location.X;
            }
        }

        [CommandProperty(AccessLevel.Counselor)]
        public int Y
        {
            get
            {
                return m_Location.Y;
            }
        }

        [CommandProperty(AccessLevel.Counselor)]
        public int Z
        {
            get
            {
                return m_Location.Z;
            }
        }

        [CommandProperty(AccessLevel.Counselor)]
        public int ItemID
        {
            get
            {
                return m_ItemID;
            }
        }
    }

}