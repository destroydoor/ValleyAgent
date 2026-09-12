using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;
using ValleyAgent.Brain;

namespace ValleyAgent.UI
{
    public class AgentMemoryPalaceMenu : IClickableMenu
    {
        private const int SidebarWidth = 200;
        private const int DetailPanelWidth = 380;
        private const int TimelineHeight = 140;
        private const int BorderThickness = 4;
        private const int Padding = 16;
        private const int CenterNodeRadius = 45;
        private const int MainNodeRadius = 35;
        private const int SubNodeHeight = 28;
        private const int SubNodePadH = 10;
        private const int NavItemHeight = 36;
        private const int BondBarH = 6;
        private const int TimelineDotSize = 28;
        private const int MaxSubNodesPerType = 6;
        private const int MaxTimelineEvents = 12;

        private static readonly Color WoodBackground = new(245, 222, 179);
        private static readonly Color WoodBorder = new(139, 105, 20);
        private static readonly Color DarkText = new(58, 42, 26);
        private static readonly Color MutedText = new(139, 115, 85);
        private static readonly Color HighlightBg = new(240, 224, 176);
        private static readonly Color ActiveNavBg = new(196, 168, 107, 51);
        private static readonly Color BondBarBg = new(224, 200, 144);
        private static readonly Color BondBarBorder = new(196, 168, 107);
        private static readonly Color QuoteBorderColor = new(218, 165, 32);
        private static readonly Color SubNodeBg = new(250, 240, 216);
        private static readonly Color SubNodeBorder = new(196, 168, 107);
        private static readonly Color SubNodeActiveBg = new(255, 232, 160);
        private static readonly Color SubNodeActiveBorder = new(255, 140, 0);
        private static readonly Color GraphAreaBg = new(232, 213, 163);
        private static readonly Color EmptyStateColor = new(160, 144, 112);
        private static readonly Color BondFillA = new(218, 165, 32);
        private static readonly Color BondFillB = new(255, 140, 0);

        private static readonly Color ConversationColor = new(74, 144, 217);
        private static readonly Color EmotionColor = new(155, 89, 182);
        private static readonly Color EventColor = new(254, 202, 87);
        private static readonly Color GiftColor = new(231, 76, 60);
        private static readonly Color DecisionColor = new(39, 174, 96);
        private static readonly Color OtherColor = new(230, 126, 34);

        private static readonly string[] NavLabels = { "人格地图", "我们的故事", "时间长河", "秘密花园" };

        private static readonly Dictionary<MemoryEntryType, Color> TypeColors = new()
        {
            { MemoryEntryType.Conversation, ConversationColor },
            { MemoryEntryType.Emotion, EmotionColor },
            { MemoryEntryType.Event, EventColor },
            { MemoryEntryType.Gift, GiftColor },
            { MemoryEntryType.Decision, DecisionColor },
            { MemoryEntryType.Generic, OtherColor },
            { MemoryEntryType.Combat, OtherColor },
            { MemoryEntryType.Farm, OtherColor },
            { MemoryEntryType.Task, OtherColor },
        };

        private static readonly Dictionary<MemoryEntryType, string> TypeLabels = new()
        {
            { MemoryEntryType.Conversation, "对话" },
            { MemoryEntryType.Emotion, "情感" },
            { MemoryEntryType.Event, "事件" },
            { MemoryEntryType.Gift, "礼物" },
            { MemoryEntryType.Decision, "决策" },
            { MemoryEntryType.Generic, "其他" },
            { MemoryEntryType.Combat, "战斗" },
            { MemoryEntryType.Farm, "农场" },
            { MemoryEntryType.Task, "任务" },
        };

        private static readonly MemoryEntryType[] LegendTypes =
        {
            MemoryEntryType.Conversation,
            MemoryEntryType.Emotion,
            MemoryEntryType.Event,
            MemoryEntryType.Gift,
            MemoryEntryType.Decision,
        };

        private readonly string _npcName;
        private readonly List<MemoryEntry> _allMemories;
        private readonly int _friendshipPoints;
        private readonly Dictionary<MemoryEntryType, List<MemoryEntry>> _memoriesByType;

        private MemoryEntry? _selectedMemory;
        private int _selectedTimelineIndex = -1;
        private int _activeNavIndex;
        private int _detailScrollOffset;
        private int _detailContentHeight;

        private Texture2D? _circleTexture;

        private Rectangle _sidebarRect;
        private Rectangle _graphRect;
        private Rectangle _detailRect;
        private Rectangle _timelineRect;

        private Vector2 _centerNodePos;
        private readonly List<MainNodeLayout> _mainNodes = new();
        private readonly List<SubNodeLayout> _subNodes = new();

        private readonly List<MemoryEntry> _timelineMemories = new();
        private readonly List<int> _timelineOriginalIndices = new();

        private static readonly RasterizerState ScissorRasterizer = new() { ScissorTestEnable = true };

        private class MainNodeLayout
        {
            public MemoryEntryType Type;
            public Vector2 Position;
            public int Count;
            public Color Color;
            public string Label = null!;
        }

        private class SubNodeLayout
        {
            public MemoryEntry Memory = null!;
            public Vector2 Position;
            public Rectangle Bounds;
            public Color ParentColor;
        }

        public AgentMemoryPalaceMenu(
            string npcName,
            List<MemoryEntry> shortTermMemories,
            List<MemoryEntry> longTermMemories,
            int friendshipPoints)
            : base(0, 0, 0, 0, showUpperRightCloseButton: false)
        {
            _npcName = npcName ?? throw new ArgumentNullException(nameof(npcName));
            _friendshipPoints = Math.Clamp(friendshipPoints, 0, 2500);

            _allMemories = new List<MemoryEntry>();
            if (shortTermMemories != null)
                _allMemories.AddRange(shortTermMemories);
            if (longTermMemories != null)
                _allMemories.AddRange(longTermMemories);

            _memoriesByType = _allMemories
                .GroupBy(m => m.EntryType)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(m => m.Timestamp).ToList());

            _timelineMemories = _allMemories
                .OrderBy(m => m.Timestamp)
                .ToList();

            for (var i = 0; i < _timelineMemories.Count; i++)
                _timelineOriginalIndices.Add(i);

            if (_timelineMemories.Count > MaxTimelineEvents)
            {
                var step = (double)(_timelineMemories.Count - 1) / (MaxTimelineEvents - 1);
                var displayMemories = new List<MemoryEntry>();
                var displayIndices = new List<int>();
                for (var i = 0; i < MaxTimelineEvents; i++)
                {
                    var idx = (int)Math.Round(step * i);
                    displayMemories.Add(_timelineMemories[idx]);
                    displayIndices.Add(idx);
                }
                _timelineMemories.Clear();
                _timelineMemories.AddRange(displayMemories);
                _timelineOriginalIndices.Clear();
                _timelineOriginalIndices.AddRange(displayIndices);
            }

            width = Game1.uiViewport.Width;
            height = Game1.uiViewport.Height;
            xPositionOnScreen = 0;
            yPositionOnScreen = 0;

            upperRightCloseButton = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + width - 40, yPositionOnScreen + 8, 32, 32),
                Game1.mouseCursors,
                new Rectangle(337, 494, 12, 12),
                2.5f);

            ComputeLayout();
            CreateTextures();
        }

        private void ComputeLayout()
        {
            _sidebarRect = new Rectangle(0, 0, SidebarWidth, height - TimelineHeight);
            _graphRect = new Rectangle(SidebarWidth, 0, width - SidebarWidth - DetailPanelWidth, height - TimelineHeight);
            _detailRect = new Rectangle(width - DetailPanelWidth, 0, DetailPanelWidth, height - TimelineHeight);
            _timelineRect = new Rectangle(0, height - TimelineHeight, width, TimelineHeight);

            _centerNodePos = new Vector2(
                _graphRect.X + _graphRect.Width / 2f,
                _graphRect.Y + _graphRect.Height / 2f);

            _mainNodes.Clear();
            _subNodes.Clear();

            var types = _memoriesByType.Keys.ToList();
            if (types.Count == 0)
                return;

            var mainRadius = Math.Min(_graphRect.Width, _graphRect.Height) * 0.28f;
            if (mainRadius < 60f) mainRadius = 60f;
            var angleStep = MathHelper.TwoPi / types.Count;
            var startAngle = -MathHelper.PiOver2;

            for (var i = 0; i < types.Count; i++)
            {
                var type = types[i];
                var angle = startAngle + angleStep * i;
                var pos = _centerNodePos + new Vector2(
                    (float)Math.Cos(angle) * mainRadius,
                    (float)Math.Sin(angle) * mainRadius);

                var color = TypeColors.TryGetValue(type, out var c) ? c : OtherColor;
                var label = TypeLabels.TryGetValue(type, out var l) ? l : type.ToString();

                _mainNodes.Add(new MainNodeLayout
                {
                    Type = type,
                    Position = pos,
                    Count = _memoriesByType[type].Count,
                    Color = color,
                    Label = label,
                });

                var memories = _memoriesByType[type].Take(MaxSubNodesPerType).ToList();
                var subRadius = 70f + Math.Min(memories.Count * 5f, 30f);
                var subSpread = MathHelper.ToRadians(Math.Min(50 + memories.Count * 8, 120));
                var subAngleStep = memories.Count > 1 ? subSpread / (memories.Count - 1) : 0;
                var subStartAngle = angle - subSpread / 2f;

                for (var j = 0; j < memories.Count; j++)
                {
                    var subAngle = memories.Count == 1 ? angle : subStartAngle + subAngleStep * j;
                    var subPos = pos + new Vector2(
                        (float)Math.Cos(subAngle) * subRadius,
                        (float)Math.Sin(subAngle) * subRadius);

                    var text = memories[j].Text;
                    var displayText = text.Length > 10 ? text[..10] + ".." : text;
                    var textSize = Game1.smallFont.MeasureString(displayText);
                    var subW = (int)textSize.X + SubNodePadH * 2;
                    var subH = SubNodeHeight;

                    _subNodes.Add(new SubNodeLayout
                    {
                        Memory = memories[j],
                        Position = subPos,
                        Bounds = new Rectangle((int)subPos.X - subW / 2, (int)subPos.Y - subH / 2, subW, subH),
                        ParentColor = color,
                    });
                }
            }
        }

        private void CreateTextures()
        {
            _circleTexture = CreateCircleTexture(128);
        }

        private static Texture2D CreateCircleTexture(int size)
        {
            var texture = new Texture2D(Game1.graphics.GraphicsDevice, size, size);
            var data = new Color[size * size];
            var radius = size / 2f;
            var r2 = radius * radius;
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var dx = x - radius + 0.5f;
                    var dy = y - radius + 0.5f;
                    data[y * size + x] = dx * dx + dy * dy <= r2
                        ? Color.White
                        : Color.Transparent;
                }
            }
            texture.SetData(data);
            return texture;
        }

        public override void draw(SpriteBatch b)
        {
            if (!Game1.options.showMenuBackground)
                b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.5f);

            DrawSidebar(b);
            DrawGraph(b);
            DrawDetailPanel(b);
            DrawTimeline(b);

            base.draw(b);
            drawMouse(b);
        }

        private void DrawSidebar(SpriteBatch b)
        {
            DrawWoodPanel(b, _sidebarRect.X, _sidebarRect.Y, _sidebarRect.Width, _sidebarRect.Height);

            var avatarSize = 48;
            var avatarX = _sidebarRect.X + Padding;
            var avatarY = _sidebarRect.Y + 20;

            var npc = Game1.getCharacterFromName(_npcName);
            if (npc?.Portrait != null)
            {
                b.Draw(npc.Portrait,
                    new Rectangle(avatarX, avatarY, avatarSize, avatarSize),
                    new Rectangle(0, 0, 64, 64), Color.White);
            }
            else
            {
                b.Draw(Game1.staminaRect,
                    new Rectangle(avatarX, avatarY, avatarSize, avatarSize),
                    GetNpcColor(_npcName) * 0.4f);
                var initial = _npcName.Length > 0 ? _npcName[0].ToString() : "?";
                var initialSize = Game1.dialogueFont.MeasureString(initial);
                b.DrawString(Game1.dialogueFont, initial,
                    new Vector2(avatarX + (avatarSize - initialSize.X) / 2, avatarY + (avatarSize - initialSize.Y) / 2),
                    Color.White * 0.8f);
            }

            DrawWoodBorder(b, avatarX - 3, avatarY - 3, avatarSize + 6, avatarSize + 6);

            var nameX = avatarX + avatarSize + 10;
            b.DrawString(Game1.smallFont, _npcName, new Vector2(nameX, avatarY + 4), DarkText);
            b.DrawString(Game1.smallFont, "内心世界", new Vector2(nameX, avatarY + 24), MutedText);

            var navY = avatarY + avatarSize + 24;
            for (var i = 0; i < NavLabels.Length; i++)
            {
                var itemRect = new Rectangle(_sidebarRect.X + 8, navY, _sidebarRect.Width - 16, NavItemHeight);
                var isHovered = itemRect.Contains(Game1.getMouseX(), Game1.getMouseY());
                var isActive = i == _activeNavIndex;

                if (isActive)
                {
                    b.Draw(Game1.staminaRect, itemRect, ActiveNavBg);
                    b.Draw(Game1.staminaRect,
                        new Rectangle(itemRect.X, itemRect.Y + (itemRect.Height - 18) / 2, 3, 18),
                        WoodBorder);
                }
                else if (isHovered)
                {
                    b.Draw(Game1.staminaRect, itemRect, WoodBorder * 0.1f);
                }

                var textSize = Game1.smallFont.MeasureString(NavLabels[i]);
                b.DrawString(Game1.smallFont, NavLabels[i],
                    new Vector2(itemRect.X + 16, itemRect.Y + (itemRect.Height - textSize.Y) / 2),
                    isActive ? DarkText : MutedText);

                navY += NavItemHeight + 4;
            }

            var bondY = _sidebarRect.Bottom - 80;
            b.Draw(Game1.staminaRect,
                new Rectangle(_sidebarRect.X, bondY - 8, _sidebarRect.Width, 2),
                BondBarBorder);

            var bondPercent = (int)(_friendshipPoints / 25.0);
            b.DrawString(Game1.smallFont, "与你的羁绊",
                new Vector2(_sidebarRect.X + Padding, bondY), MutedText);
            b.DrawString(Game1.dialogueFont, $"亲密度 {bondPercent}%",
                new Vector2(_sidebarRect.X + Padding, bondY + 18), WoodBorder);

            var barX = _sidebarRect.X + Padding;
            var barY = bondY + 48;
            var barW = _sidebarRect.Width - Padding * 2;
            b.Draw(Game1.staminaRect, new Rectangle(barX, barY, barW, BondBarH), BondBarBg);
            var fillW = (int)(barW * _friendshipPoints / 2500.0);
            if (fillW > 0)
            {
                for (var px = 0; px < fillW; px++)
                {
                    var t = fillW > 1 ? (float)px / (fillW - 1) : 0f;
                    var r = (byte)(BondFillA.R + (BondFillB.R - BondFillA.R) * t);
                    var g = (byte)(BondFillA.G + (BondFillB.G - BondFillA.G) * t);
                    var bb = (byte)(BondFillA.B + (BondFillB.B - BondFillA.B) * t);
                    b.Draw(Game1.staminaRect, new Rectangle(barX + px, barY, 1, BondBarH), new Color(r, g, bb));
                }
            }
            DrawRectBorder(b, new Rectangle(barX - 1, barY - 1, barW + 2, BondBarH + 2), BondBarBorder, 1);
        }

        private void DrawGraph(SpriteBatch b)
        {
            b.Draw(Game1.staminaRect, _graphRect, GraphAreaBg);

            var titleX = _graphRect.X + 24;
            b.DrawString(Game1.dialogueFont, "人格神经图谱",
                new Vector2(titleX, _graphRect.Y + 20), DarkText);
            b.DrawString(Game1.smallFont, $"记忆相互连接，构成了现在的{_npcName}",
                new Vector2(titleX, _graphRect.Y + 50), MutedText);

            if (_allMemories.Count == 0)
            {
                var emptyText = "尚无记忆数据";
                var emptySize = Game1.dialogueFont.MeasureString(emptyText);
                b.DrawString(Game1.dialogueFont, emptyText,
                    new Vector2(_centerNodePos.X - emptySize.X / 2, _centerNodePos.Y - emptySize.Y / 2),
                    EmptyStateColor);
                DrawWoodBorder(b, _graphRect.X, _graphRect.Y, _graphRect.Width, _graphRect.Height);
                return;
            }

            foreach (var mainNode in _mainNodes)
            {
                DrawConnectionLine(b, _centerNodePos, mainNode.Position, mainNode.Color, 2f, 0.5f, false);
            }

            foreach (var subNode in _subNodes)
            {
                var parentNode = _mainNodes.FirstOrDefault(mn => mn.Type == subNode.Memory.EntryType);
                if (parentNode != null)
                {
                    DrawConnectionLine(b, parentNode.Position, subNode.Position, subNode.ParentColor, 1f, 0.3f, true);
                }
            }

            DrawCircleNode(b, _centerNodePos, CenterNodeRadius, WoodBackground, WoodBorder, 4);

            if (npcPortraitAvailable())
            {
                var npc = Game1.getCharacterFromName(_npcName);
                if (npc?.Portrait != null)
                {
                    var inset = 8;
                    var portraitSize = (CenterNodeRadius - inset) * 2;
                    b.Draw(npc.Portrait,
                        new Rectangle((int)_centerNodePos.X - portraitSize / 2, (int)_centerNodePos.Y - portraitSize / 2, portraitSize, portraitSize),
                        new Rectangle(0, 0, 64, 64), Color.White);
                }
            }
            else
            {
                var initial = _npcName.Length > 0 ? _npcName[0].ToString() : "?";
                var initialSize = Game1.dialogueFont.MeasureString(initial);
                b.DrawString(Game1.dialogueFont, initial,
                    new Vector2(_centerNodePos.X - initialSize.X / 2, _centerNodePos.Y - initialSize.Y / 2),
                    DarkText);
            }

            var nameLabelSize = Game1.smallFont.MeasureString(_npcName);
            b.DrawString(Game1.smallFont, _npcName,
                new Vector2(_centerNodePos.X - nameLabelSize.X / 2, _centerNodePos.Y + CenterNodeRadius + 6),
                DarkText);

            foreach (var mainNode in _mainNodes)
            {
                DrawCircleNode(b, mainNode.Position, MainNodeRadius, WoodBackground, mainNode.Color, 3);

                var labelSize = Game1.smallFont.MeasureString(mainNode.Label);
                b.DrawString(Game1.smallFont, mainNode.Label,
                    new Vector2(mainNode.Position.X - labelSize.X / 2, mainNode.Position.Y - 8),
                    mainNode.Color);

                var countText = mainNode.Count.ToString();
                var countSize = Game1.smallFont.MeasureString(countText);
                b.DrawString(Game1.smallFont, countText,
                    new Vector2(mainNode.Position.X - countSize.X / 2, mainNode.Position.Y + 6),
                    MutedText);
            }

            foreach (var subNode in _subNodes)
            {
                var isSelected = subNode.Memory == _selectedMemory;
                var isHovered = subNode.Bounds.Contains(Game1.getMouseX(), Game1.getMouseY());

                var bg = isSelected ? SubNodeActiveBg : isHovered ? HighlightBg : SubNodeBg;
                var border = isSelected ? SubNodeActiveBorder : isHovered ? QuoteBorderColor : SubNodeBorder;

                b.Draw(Game1.staminaRect, subNode.Bounds, bg);
                DrawRectBorder(b, subNode.Bounds, border, 2);

                var text = subNode.Memory.Text;
                var displayText = text.Length > 10 ? text[..10] + ".." : text;
                var textSize = Game1.smallFont.MeasureString(displayText);
                b.DrawString(Game1.smallFont, displayText,
                    new Vector2(subNode.Bounds.X + (subNode.Bounds.Width - textSize.X) / 2,
                                subNode.Bounds.Y + (subNode.Bounds.Height - textSize.Y) / 2),
                    isSelected ? DarkText : MutedText);
            }

            var legendY = _graphRect.Bottom - 28;
            var legendX = _graphRect.X + 24;
            foreach (var type in LegendTypes)
            {
                var color = TypeColors[type];
                var label = TypeLabels[type];
                b.Draw(Game1.staminaRect, new Rectangle(legendX, legendY + 3, 8, 8), color);
                var lblSize = Game1.smallFont.MeasureString(label);
                b.DrawString(Game1.smallFont, label, new Vector2(legendX + 12, legendY), MutedText);
                legendX += (int)lblSize.X + 24;
            }

            DrawWoodBorder(b, _graphRect.X, _graphRect.Y, _graphRect.Width, _graphRect.Height);
        }

        private void DrawDetailPanel(SpriteBatch b)
        {
            DrawWoodPanel(b, _detailRect.X, _detailRect.Y, _detailRect.Width, _detailRect.Height);

            if (_selectedMemory == null)
            {
                var emptyText1 = "点击节点查看记忆详情";
                var emptyText2 = $"探索 {_npcName} 的内心世界";
                var size1 = Game1.smallFont.MeasureString(emptyText1);
                var size2 = Game1.smallFont.MeasureString(emptyText2);
                var centerX = _detailRect.X + _detailRect.Width / 2f;
                var centerY = _detailRect.Y + _detailRect.Height / 2f;
                b.DrawString(Game1.smallFont, emptyText1,
                    new Vector2(centerX - size1.X / 2, centerY - 20), EmptyStateColor);
                b.DrawString(Game1.smallFont, emptyText2,
                    new Vector2(centerX - size2.X / 2, centerY + 10), MutedText);
                return;
            }

            var curY = _detailRect.Y + Padding;

            var typeLabel = TypeLabels.TryGetValue(_selectedMemory.EntryType, out var tl) ? tl : _selectedMemory.EntryType.ToString();
            var tagSize = Game1.smallFont.MeasureString(typeLabel);
            var tagRect = new Rectangle(_detailRect.X + Padding, curY, (int)tagSize.X + 16, (int)tagSize.Y + 6);
            b.Draw(Game1.staminaRect, tagRect, WoodBorder * 0.15f);
            DrawRectBorder(b, tagRect, BondBarBorder, 1);
            b.DrawString(Game1.smallFont, typeLabel,
                new Vector2(tagRect.X + 8, tagRect.Y + 3), WoodBorder);
            curY += tagRect.Height + 8;

            var titleText = _selectedMemory.Text.Length > 30
                ? _selectedMemory.Text[..30] + ".."
                : _selectedMemory.Text;
            b.DrawString(Game1.dialogueFont, titleText,
                new Vector2(_detailRect.X + Padding, curY), DarkText);
            curY += 30;

            var timeText = _selectedMemory.Timestamp.ToString("yyyy/MM/dd HH:mm");
            b.DrawString(Game1.smallFont, timeText,
                new Vector2(_detailRect.X + Padding, curY), MutedText);
            curY += 24;

            b.Draw(Game1.staminaRect,
                new Rectangle(_detailRect.X + Padding, curY, _detailRect.Width - Padding * 2, 2),
                BondBarBorder);
            curY += 8;

            var contentArea = new Rectangle(
                _detailRect.X + Padding,
                curY,
                _detailRect.Width - Padding * 2,
                _detailRect.Bottom - curY - Padding);

            b.End();
            b.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, null, ScissorRasterizer);

            var originalScissor = b.GraphicsDevice.ScissorRectangle;
            b.GraphicsDevice.ScissorRectangle = contentArea;

            var contentY = contentArea.Y - _detailScrollOffset;

            var quoteWrapWidth = contentArea.Width - 28;
            var wrappedQuote = WrapText(Game1.smallFont, _selectedMemory.Text, quoteWrapWidth);
            var wrappedSize = Game1.smallFont.MeasureString(wrappedQuote);
            var quoteHeight = (int)wrappedSize.Y + 24;

            var quoteRect = new Rectangle(contentArea.X, contentY, contentArea.Width, quoteHeight);
            b.Draw(Game1.staminaRect, quoteRect, WoodBorder * 0.08f);
            b.Draw(Game1.staminaRect,
                new Rectangle(quoteRect.X, quoteRect.Y, 3, quoteRect.Height),
                QuoteBorderColor);
            b.DrawString(Game1.smallFont, wrappedQuote,
                new Vector2(quoteRect.X + 14, quoteRect.Y + 12), DarkText);
            contentY += quoteHeight + 12;

            b.DrawString(Game1.smallFont, "想法",
                new Vector2(contentArea.X, contentY), MutedText);
            contentY += 20;

            if (_selectedMemory.Tags is { Count: > 0 })
            {
                var tagX = contentArea.X;
                foreach (var tag in _selectedMemory.Tags)
                {
                    var pillSize = Game1.smallFont.MeasureString(tag);
                    var pillW = (int)pillSize.X + 12;
                    var pillH = (int)pillSize.Y + 6;

                    if (tagX + pillW > contentArea.Right)
                    {
                        tagX = contentArea.X;
                        contentY += pillH + 4;
                    }

                    b.Draw(Game1.staminaRect, new Rectangle(tagX, contentY, pillW, pillH), WoodBorder * 0.1f);
                    b.DrawString(Game1.smallFont, tag,
                        new Vector2(tagX + 6, contentY + 3), DarkText);
                    tagX += pillW + 6;
                }
                contentY += 28;
            }
            else
            {
                b.DrawString(Game1.smallFont, "无",
                    new Vector2(contentArea.X + 12, contentY), MutedText);
                contentY += 24;
            }

            contentY += 12;

            b.DrawString(Game1.smallFont, "影响",
                new Vector2(contentArea.X, contentY), MutedText);
            contentY += 20;

            var impBarW = contentArea.Width - 60;
            var impBarH = 12;
            var impFill = (int)(impBarW * Math.Clamp(_selectedMemory.Importance / 10.0, 0, 1));

            b.Draw(Game1.staminaRect,
                new Rectangle(contentArea.X, contentY, impBarW, impBarH), BondBarBg);
            if (impFill > 0)
            {
                var impColor = _selectedMemory.Importance > 7 ? GiftColor
                    : _selectedMemory.Importance > 4 ? EventColor
                    : DecisionColor;
                b.Draw(Game1.staminaRect,
                    new Rectangle(contentArea.X, contentY, impFill, impBarH), impColor);
            }
            DrawRectBorder(b, new Rectangle(contentArea.X, contentY, impBarW, impBarH), BondBarBorder, 1);

            var impText = $"{_selectedMemory.Importance:F1} / 10.0";
            b.DrawString(Game1.smallFont, impText,
                new Vector2(contentArea.X + impBarW + 8, contentY - 2), DarkText);
            contentY += impBarH + 16;

            if (!string.IsNullOrEmpty(_selectedMemory.Location))
            {
                contentY += 12;
                b.DrawString(Game1.smallFont, "地点",
                    new Vector2(contentArea.X, contentY), MutedText);
                contentY += 20;
                b.DrawString(Game1.smallFont, _selectedMemory.Location,
                    new Vector2(contentArea.X + 12, contentY), DarkText);
                contentY += 24;
            }

            _detailContentHeight = contentY - contentArea.Y + _detailScrollOffset;

            b.GraphicsDevice.ScissorRectangle = originalScissor;
            b.End();
            b.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, null, null);
        }

        private void DrawTimeline(SpriteBatch b)
        {
            DrawWoodPanel(b, _timelineRect.X, _timelineRect.Y, _timelineRect.Width, _timelineRect.Height);

            var headerY = _timelineRect.Y + 12;
            b.DrawString(Game1.smallFont, "时间长河",
                new Vector2(_timelineRect.X + 24, headerY), DarkText);
            b.DrawString(Game1.smallFont, "回顾你们一起走过的每个瞬间",
                new Vector2(_timelineRect.X + 100, headerY), MutedText);
            headerY += 24;

            if (_timelineMemories.Count == 0)
            {
                b.DrawString(Game1.smallFont, "暂无时间线数据",
                    new Vector2(_timelineRect.X + 24, headerY + 20), EmptyStateColor);
                return;
            }

            var lineY = headerY + 25;
            var lineStartX = _timelineRect.X + 40;
            var lineEndX = _timelineRect.Right - 40;
            b.Draw(Game1.staminaRect,
                new Rectangle(lineStartX, lineY, lineEndX - lineStartX, 2),
                BondBarBorder);

            var count = _timelineMemories.Count;
            var spacing = count > 1 ? (lineEndX - lineStartX) / (count - 1) : 0;

            for (var i = 0; i < count; i++)
            {
                var memory = _timelineMemories[i];
                var eventX = count == 1 ? (lineStartX + lineEndX) / 2 : lineStartX + spacing * i;
                var isSelected = i == _selectedTimelineIndex;
                var isHovered = Math.Abs(Game1.getMouseX() - eventX) < TimelineDotSize / 2 + 4
                    && Math.Abs(Game1.getMouseY() - lineY) < TimelineDotSize / 2 + 4;

                var dotColor = isSelected ? SubNodeActiveBg : isHovered ? HighlightBg : BondBarBg;
                var dotBorderColor = isSelected ? SubNodeActiveBorder : isHovered ? QuoteBorderColor : BondBarBorder;

                DrawCircleNode(b, new Vector2(eventX, lineY), TimelineDotSize / 2, dotColor, dotBorderColor, 2);

                var typeIcon = TypeLabels.TryGetValue(memory.EntryType, out var icon) ? icon[..1] : "*";
                var iconSize = Game1.smallFont.MeasureString(typeIcon);
                b.DrawString(Game1.smallFont, typeIcon,
                    new Vector2(eventX - iconSize.X / 2, lineY - iconSize.Y / 2),
                    DarkText);

                var descText = memory.Text.Length > 6 ? memory.Text[..6] + ".." : memory.Text;
                var descSize = Game1.smallFont.MeasureString(descText);
                b.DrawString(Game1.smallFont, descText,
                    new Vector2(eventX - descSize.X / 2, lineY + TimelineDotSize / 2 + 4),
                    isSelected ? DarkText : MutedText);

                var dateText = memory.Timestamp.ToString("MM/dd");
                var dateSize = Game1.smallFont.MeasureString(dateText);
                b.DrawString(Game1.smallFont, dateText,
                    new Vector2(eventX - dateSize.X / 2, lineY - TimelineDotSize / 2 - 16),
                    MutedText);
            }

            var currentText = $"当前时间：{Game1.currentSeason} 第{Game1.dayOfMonth}天";
            var currentSize = Game1.smallFont.MeasureString(currentText);
            b.DrawString(Game1.smallFont, currentText,
                new Vector2(_timelineRect.X + (_timelineRect.Width - currentSize.X) / 2, _timelineRect.Bottom - 24),
                MutedText);
        }

        private void DrawCircleNode(SpriteBatch b, Vector2 center, int radius, Color fill, Color border, int borderWidth)
        {
            if (_circleTexture == null) return;

            var outerRadius = radius + borderWidth;
            var outerDest = new Rectangle(
                (int)center.X - outerRadius,
                (int)center.Y - outerRadius,
                outerRadius * 2,
                outerRadius * 2);
            b.Draw(_circleTexture, outerDest, border);

            var innerDest = new Rectangle(
                (int)center.X - radius,
                (int)center.Y - radius,
                radius * 2,
                radius * 2);
            b.Draw(_circleTexture, innerDest, fill);
        }

        private static void DrawWoodPanel(SpriteBatch b, int x, int y, int w, int h)
        {
            b.Draw(Game1.staminaRect, new Rectangle(x, y, w, h), WoodBackground);
            DrawWoodBorder(b, x, y, w, h);
        }

        private static void DrawWoodBorder(SpriteBatch b, int x, int y, int w, int h)
        {
            b.Draw(Game1.staminaRect, new Rectangle(x, y, w, BorderThickness), WoodBorder);
            b.Draw(Game1.staminaRect, new Rectangle(x, y + h - BorderThickness, w, BorderThickness), WoodBorder);
            b.Draw(Game1.staminaRect, new Rectangle(x, y, BorderThickness, h), WoodBorder);
            b.Draw(Game1.staminaRect, new Rectangle(x + w - BorderThickness, y, BorderThickness, h), WoodBorder);
        }

        private static void DrawRectBorder(SpriteBatch b, Rectangle rect, Color color, int thickness)
        {
            b.Draw(Game1.staminaRect, new Rectangle(rect.X, rect.Y, rect.Width, thickness), color);
            b.Draw(Game1.staminaRect, new Rectangle(rect.X, rect.Bottom - thickness, rect.Width, thickness), color);
            b.Draw(Game1.staminaRect, new Rectangle(rect.X, rect.Y, thickness, rect.Height), color);
            b.Draw(Game1.staminaRect, new Rectangle(rect.Right - thickness, rect.Y, thickness, rect.Height), color);
        }

        private static void DrawConnectionLine(SpriteBatch b, Vector2 start, Vector2 end, Color color, float thickness, float alpha, bool dashed)
        {
            var drawColor = color * alpha;
            if (!dashed)
            {
                DrawLine(b, start, end, drawColor, thickness);
                return;
            }

            var delta = end - start;
            var length = delta.Length();
            if (length < 1f) return;

            var step = delta / length;
            var drawn = 0f;
            var drawing = true;
            var dashLen = 8f;
            var gapLen = 6f;

            while (drawn < length)
            {
                if (drawing)
                {
                    var segEnd = Math.Min(drawn + dashLen, length);
                    DrawLine(b, start + step * drawn, start + step * segEnd, drawColor, thickness);
                    drawn = segEnd;
                }
                else
                {
                    drawn += gapLen;
                }
                drawing = !drawing;
            }
        }

        private static void DrawLine(SpriteBatch b, Vector2 start, Vector2 end, Color color, float thickness)
        {
            var delta = end - start;
            var angle = (float)Math.Atan2(delta.Y, delta.X);
            var length = delta.Length();
            if (length < 0.5f) return;

            b.Draw(Game1.staminaRect,
                start,
                null,
                color,
                angle,
                Vector2.Zero,
                new Vector2(length, thickness),
                SpriteEffects.None,
                0f);
        }

        private static string WrapText(SpriteFont font, string text, float maxWidth)
        {
            if (string.IsNullOrEmpty(text)) return text;

            var result = new StringBuilder();
            var line = "";

            foreach (var ch in text)
            {
                var testLine = line + ch;
                if (font.MeasureString(testLine).X > maxWidth && line.Length > 0)
                {
                    result.AppendLine(line);
                    line = ch.ToString();
                }
                else
                {
                    line = testLine;
                }
            }

            if (line.Length > 0)
                result.Append(line);

            return result.ToString();
        }

        private bool npcPortraitAvailable()
        {
            return Game1.getCharacterFromName(_npcName)?.Portrait != null;
        }

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            base.receiveLeftClick(x, y, playSound);

            foreach (var subNode in _subNodes)
            {
                if (subNode.Bounds.Contains(x, y))
                {
                    SelectMemory(subNode.Memory);
                    return;
                }
            }

            if (_timelineMemories.Count > 0)
            {
                var lineY = _timelineRect.Y + 36 + 25;
                var lineStartX = _timelineRect.X + 40;
                var lineEndX = _timelineRect.Right - 40;
                var count = _timelineMemories.Count;
                var spacing = count > 1 ? (lineEndX - lineStartX) / (count - 1) : 0;

                for (var i = 0; i < count; i++)
                {
                    var eventX = count == 1 ? (lineStartX + lineEndX) / 2 : lineStartX + spacing * i;
                    if (Math.Abs(x - eventX) < TimelineDotSize / 2 + 4
                        && Math.Abs(y - lineY) < TimelineDotSize / 2 + 4)
                    {
                        SelectMemory(_timelineMemories[i]);
                        _selectedTimelineIndex = i;
                        return;
                    }
                }
            }

            var navY = _sidebarRect.Y + 20 + 48 + 24;
            for (var i = 0; i < NavLabels.Length; i++)
            {
                var itemRect = new Rectangle(_sidebarRect.X + 8, navY, _sidebarRect.Width - 16, NavItemHeight);
                if (itemRect.Contains(x, y))
                {
                    _activeNavIndex = i;
                    return;
                }
                navY += NavItemHeight + 4;
            }
        }

        private void SelectMemory(MemoryEntry memory)
        {
            _selectedMemory = memory;
            _detailScrollOffset = 0;

            var idx = _timelineMemories.IndexOf(memory);
            if (idx >= 0)
                _selectedTimelineIndex = idx;
        }

        public override void receiveKeyPress(Keys key)
        {
            if (key == Keys.Escape)
            {
                exitThisMenu();
                return;
            }

            base.receiveKeyPress(key);
        }

        public override void receiveScrollWheelAction(int direction)
        {
            base.receiveScrollWheelAction(direction);

            if (_detailRect.Contains(Game1.getMouseX(), Game1.getMouseY()))
            {
                _detailScrollOffset -= direction * 20;
                _detailScrollOffset = Math.Max(0, _detailScrollOffset);

                var contentAreaH = _detailRect.Height - Padding - 120;
                var maxScroll = Math.Max(0, _detailContentHeight - contentAreaH);
                _detailScrollOffset = Math.Min(_detailScrollOffset, maxScroll);
            }
        }

        public override void gameWindowSizeChanged(Rectangle oldBounds, Rectangle newBounds)
        {
            width = Game1.uiViewport.Width;
            height = Game1.uiViewport.Height;
            xPositionOnScreen = 0;
            yPositionOnScreen = 0;

            if (upperRightCloseButton != null)
            {
                upperRightCloseButton.bounds = new Rectangle(width - 40, 8, 32, 32);
            }

            ComputeLayout();
        }

        public override void emergencyShutDown()
        {
            DisposeTextures();
            base.emergencyShutDown();
        }

        protected override void cleanupBeforeExit()
        {
            DisposeTextures();
            base.cleanupBeforeExit();
        }

        private void DisposeTextures()
        {
            _circleTexture?.Dispose();
            _circleTexture = null;
        }

        private static Color GetNpcColor(string name)
        {
            var hash = 0;
            foreach (var c in name)
                hash = (hash * 31 + c) & 0xFFFFFF;

            var r = (byte)(80 + (hash & 0xFF) % 120);
            var g = (byte)(80 + ((hash >> 8) & 0xFF) % 120);
            var bb = (byte)(80 + ((hash >> 16) & 0xFF) % 120);
            return new Color(r, g, bb);
        }
    }
}
