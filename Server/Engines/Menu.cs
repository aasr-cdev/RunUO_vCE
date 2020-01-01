using System;
using Server.Network;

namespace Server.Menus
{
    public interface IMenu
    {
        int Serial { get; }
        int EntryLength { get; }
        void SendTo(NetState state);
        void OnCancel(NetState state);
        void OnResponse(NetState state, int index);
    }
}

namespace Server.Menus.ItemLists
{
    public class ItemListEntry
    {
        private string m_Name;
        private int m_ItemID;
        private int m_Hue;

        public string Name
        {
            get
            {
                return m_Name;
            }
        }

        public int ItemID
        {
            get
            {
                return m_ItemID;
            }
        }

        public int Hue
        {
            get
            {
                return m_Hue;
            }
        }

        public ItemListEntry(string name, int itemID) : this(name, itemID, 0)
        {
        }

        public ItemListEntry(string name, int itemID, int hue)
        {
            m_Name = name;
            m_ItemID = itemID;
            m_Hue = hue;
        }
    }

    public class ItemListMenu : IMenu
    {
        private string m_Question;
        private ItemListEntry[] m_Entries;

        private int m_Serial;
        private static int m_NextSerial;

        int IMenu.Serial
        {
            get
            {
                return m_Serial;
            }
        }

        int IMenu.EntryLength
        {
            get
            {
                return m_Entries.Length;
            }
        }

        public string Question
        {
            get
            {
                return m_Question;
            }
        }

        public ItemListEntry[] Entries
        {
            get
            {
                return m_Entries;
            }
            set
            {
                m_Entries = value;
            }
        }

        public ItemListMenu(string question, ItemListEntry[] entries)
        {
            m_Question = question;
            m_Entries = entries;

            do
            {
                m_Serial = m_NextSerial++;
                m_Serial &= 0x7FFFFFFF;
            } while (m_Serial == 0);

            m_Serial = (int)((uint)m_Serial | 0x80000000);
        }

        public virtual void OnCancel(NetState state)
        {
        }

        public virtual void OnResponse(NetState state, int index)
        {
        }

        public void SendTo(NetState state)
        {
            state.AddMenu(this);
            state.Send(new DisplayItemListMenu(this));
        }
    }
}

namespace Server.Menus.Questions
{
    public class QuestionMenu : IMenu
    {
        private string m_Question;
        private string[] m_Answers;

        private int m_Serial;
        private static int m_NextSerial;

        int IMenu.Serial
        {
            get
            {
                return m_Serial;
            }
        }

        int IMenu.EntryLength
        {
            get
            {
                return m_Answers.Length;
            }
        }

        public string Question
        {
            get
            {
                return m_Question;
            }
            set
            {
                m_Question = value;
            }
        }

        public string[] Answers
        {
            get
            {
                return m_Answers;
            }
        }

        public QuestionMenu(string question, string[] answers)
        {
            m_Question = question;
            m_Answers = answers;

            do
            {
                m_Serial = ++m_NextSerial;
                m_Serial &= 0x7FFFFFFF;
            } while (m_Serial == 0);
        }

        public virtual void OnCancel(NetState state)
        {
        }

        public virtual void OnResponse(NetState state, int index)
        {
        }

        public void SendTo(NetState state)
        {
            state.AddMenu(this);
            state.Send(new DisplayQuestionMenu(this));
        }
    }
}