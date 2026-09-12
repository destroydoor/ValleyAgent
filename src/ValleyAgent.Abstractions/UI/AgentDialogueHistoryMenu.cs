using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;
using ValleyAgent.Save.Models;

namespace ValleyAgent.UI
{
    public class AgentDialogueHistoryMenu : IClickableMenu
    {
        private readonly string _npcName;
        private readonly List<DialogueExchangeData> _dialogueHistory;
        private int _scrollOffset;
        private int _contentHeight;

        private const int MenuWidth = 600;
        private const int MenuHeight = 500;
        private const int TitleHeight = 50;
        private const int Padding = 16;
        private const int ScrollBarAreaWidth = 36;
        private const int MessagePadding = 10;
        private const int BorderThickness = 4;
        private const int SideBorderWidth = 4;
        private const int ScrollStep = 40;
        private const float BubbleWidthRatio = 0.75f;

        private static readonly Color WoodBackground = new(245, 222, 179);
        private static readonly Color WoodBorder = new(139, 105, 20);
        private static readonly Color DarkText = new(58, 42, 26);
        private static readonly Color MutedText = new(139, 115, 85);
        private static readonly Color NpcBubbleBg = new(45, 40, 70);
        private static readonly Color NpcBorderColor = new(139, 105, 20);
        private static readonly Color PlayerBubbleBg = new(30, 60, 35);
        private static readonly Color PlayerBorderColor = new(60, 140, 60);
        private static readonly Color NpcHeaderColor = new(180, 170, 220);
        private static readonly Color PlayerHeaderColor = new(140, 200, 140);

        private Rectangle _contentArea;
        private Rectangle _scrollTrack;
        private Rectangle _upArrowRect;
        private Rectangle _downArrowRect;

        private static readonly RasterizerState ScissorRasterizer = new() { ScissorTestEnable = true };

        public AgentDialogueHistoryMenu(string npcName, List<DialogueExchangeData> dialogueHistory)
            : base(0, 0, 0, 0, showUpperRightCloseButton: false)
        {
            _npcName = npcName;
            _dialogueHistory = dialogueHistory ?? new List<DialogueExchangeData>();

            width = MenuWidth;
            height = MenuHeight;
            xPositionOnScreen = (Game1.uiViewport.Width - width) / 2;
            yPositionOnScreen = (Game1.uiViewport.Height - height) / 2;

            _contentArea = new Rectangle(
                xPositionOnScreen + Padding,
                yPositionOnScreen + TitleHeight,
                width - Padding * 2 - ScrollBarAreaWidth,
                height - TitleHeight - Padding);

            _scrollTrack = new Rectangle(
                xPositionOnScreen + width - Padding - ScrollBarAreaWidth + 8,
                yPositionOnScreen + TitleHeight + 28,
                20,
                _contentArea.Height - 56);

            _upArrowRect = new Rectangle(_scrollTrack.X - 2, _scrollTrack.Y - 24, 24, 20);
            _downArrowRect = new Rectangle(_scrollTrack.X - 2, _scrollTrack.Bottom + 4, 24, 20);

            upperRightCloseButton = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + width - 40, yPositionOnScreen + 8, 32, 32),
                Game1.mouseCursors,
                new Rectangle(337, 494, 12, 12),
                2.5f);

            CalculateContentHeight();
        }

        private void CalculateContentHeight()
        {
            _contentHeight = 0;
            var messageWidth = _contentArea.Width - MessagePadding * 2;
            var bubbleWidth = messageWidth * BubbleWidthRatio;
            var innerWidth = bubbleWidth - SideBorderWidth - MessagePadding * 2;

            foreach (var exchange in _dialogueHistory)
            {
                if (!string.IsNullOrEmpty(exchange.NpcResponse))
                {
                    var wrapped = WrapText(Game1.smallFont, exchange.NpcResponse, innerWidth);
                    var lines = wrapped.Split('\n').Length;
                    _contentHeight += 18 + lines * Game1.smallFont.LineSpacing + MessagePadding * 2 + 8;
                }

                if (!string.IsNullOrEmpty(exchange.PlayerInput))
                {
                    var wrapped = WrapText(Game1.smallFont, exchange.PlayerInput, innerWidth);
                    var lines = wrapped.Split('\n').Length;
                    _contentHeight += 18 + lines * Game1.smallFont.LineSpacing + MessagePadding * 2 + 8;
                }
            }

            ClampScrollOffset();
        }

        private void ClampScrollOffset()
        {
            var maxScroll = Math.Max(0, _contentHeight - _contentArea.Height);
            _scrollOffset = Math.Max(0, Math.Min(_scrollOffset, maxScroll));
        }

        private static string WrapText(SpriteFont font, string text, float maxWidth)
        {
            if (string.IsNullOrEmpty(text) || maxWidth <= 0) return text ?? string.Empty;

            var result = new StringBuilder();
            var lines = text.Split('\n');

            foreach (var line in lines)
            {
                if (string.IsNullOrEmpty(line))
                {
                    result.AppendLine();
                    continue;
                }

                var currentLine = string.Empty;
                var words = line.Split(' ');

                foreach (var word in words)
                {
                    var testLine = currentLine.Length == 0 ? word : currentLine + " " + word;
                    if (font.MeasureString(testLine).X > maxWidth && currentLine.Length > 0)
                    {
                        result.AppendLine(currentLine);
                        currentLine = word;
                    }
                    else
                    {
                        currentLine = testLine;
                    }
                }

                if (currentLine.Length > 0)
                    result.AppendLine(currentLine);
            }

            return result.ToString().TrimEnd('\n', '\r');
        }

        public override void draw(SpriteBatch b)
        {
            if (!Game1.options.showMenuBackground)
                b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.5f);

            DrawWoodPanel(b, xPositionOnScreen, yPositionOnScreen, width, height);

            var title = $"与 {_npcName} 的对话记录";
            var titleSize = Game1.dialogueFont.MeasureString(title);
            b.DrawString(Game1.dialogueFont, title,
                new Vector2(xPositionOnScreen + (width - titleSize.X) / 2, yPositionOnScreen + (TitleHeight - titleSize.Y) / 2),
                DarkText);

            DrawMessages(b);
            DrawScrollBar(b);

            base.draw(b);
            drawMouse(b);
        }

        private void DrawMessages(SpriteBatch b)
        {
            var originalScissor = b.GraphicsDevice.ScissorRectangle;
            b.End();
            b.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, null, ScissorRasterizer);
            b.GraphicsDevice.ScissorRectangle = _contentArea;

            if (_dialogueHistory.Count == 0)
            {
                var emptyText = "暂无对话记录";
                var textSize = Game1.smallFont.MeasureString(emptyText);
                b.DrawString(Game1.smallFont, emptyText,
                    new Vector2(_contentArea.X + (_contentArea.Width - textSize.X) / 2,
                                _contentArea.Y + (_contentArea.Height - textSize.Y) / 2),
                    MutedText);
            }
            else
            {
                var y = _contentArea.Y - _scrollOffset;
                var messageWidth = _contentArea.Width - MessagePadding * 2;

                foreach (var exchange in _dialogueHistory)
                {
                    if (y >= _contentArea.Bottom) break;

                    if (!string.IsNullOrEmpty(exchange.NpcResponse))
                    {
                        y = DrawMessageBubble(b, exchange.NpcResponse, _npcName, exchange.Timestamp,
                            y, messageWidth, isNpc: true);
                    }

                    if (y >= _contentArea.Bottom) break;

                    if (!string.IsNullOrEmpty(exchange.PlayerInput))
                    {
                        y = DrawMessageBubble(b, exchange.PlayerInput, "你", exchange.Timestamp,
                            y, messageWidth, isNpc: false);
                    }
                }
            }

            b.End();
            b.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, null, null);
            b.GraphicsDevice.ScissorRectangle = originalScissor;
        }

        private int DrawMessageBubble(SpriteBatch b, string text, string speaker, DateTime timestamp,
            int startY, float maxWidth, bool isNpc)
        {
            var bubbleWidth = (int)(maxWidth * BubbleWidthRatio);
            var innerWidth = bubbleWidth - SideBorderWidth - MessagePadding * 2;
            var wrappedText = WrapText(Game1.smallFont, text, innerWidth);
            var textLines = wrappedText.Split('\n');
            var textHeight = textLines.Length * Game1.smallFont.LineSpacing;
            const int headerHeight = 18;
            var bubbleHeight = headerHeight + textHeight + MessagePadding * 2;

            if (startY + bubbleHeight < _contentArea.Y)
                return startY + bubbleHeight + 8;

            int bubbleX;
            if (isNpc)
                bubbleX = _contentArea.X + MessagePadding;
            else
                bubbleX = _contentArea.Right - MessagePadding - bubbleWidth;

            var bubbleRect = new Rectangle(bubbleX, startY, bubbleWidth, bubbleHeight);
            var bgColor = isNpc ? NpcBubbleBg : PlayerBubbleBg;
            var borderColor = isNpc ? NpcBorderColor : PlayerBorderColor;

            b.Draw(Game1.staminaRect, bubbleRect, bgColor);

            if (isNpc)
                b.Draw(Game1.staminaRect,
                    new Rectangle(bubbleX, startY, SideBorderWidth, bubbleHeight), borderColor);
            else
                b.Draw(Game1.staminaRect,
                    new Rectangle(bubbleX + bubbleWidth - SideBorderWidth, startY, SideBorderWidth, bubbleHeight),
                    borderColor);

            var timeStr = timestamp.ToString("HH:mm");
            var headerText = $"{speaker}  {timeStr}";
            var textStartX = bubbleX + MessagePadding + (isNpc ? SideBorderWidth : 0);
            b.DrawString(Game1.smallFont, headerText,
                new Vector2(textStartX, startY + 4),
                isNpc ? NpcHeaderColor : PlayerHeaderColor);

            var textY = startY + headerHeight + MessagePadding;
            for (var i = 0; i < textLines.Length; i++)
            {
                b.DrawString(Game1.smallFont, textLines[i],
                    new Vector2(textStartX, textY + i * Game1.smallFont.LineSpacing),
                    Color.White * 0.9f);
            }

            return startY + bubbleHeight + 8;
        }

        private void DrawScrollBar(SpriteBatch b)
        {
            var maxScroll = _contentHeight - _contentArea.Height;
            if (maxScroll <= 0) return;

            b.Draw(Game1.staminaRect, _scrollTrack, WoodBorder * 0.3f);

            var ratio = (float)_contentArea.Height / _contentHeight;
            var thumbHeight = Math.Max(20, (int)(_scrollTrack.Height * ratio));
            var scrollRatio = (float)_scrollOffset / maxScroll;
            var thumbY = _scrollTrack.Y + (int)((_scrollTrack.Height - thumbHeight) * scrollRatio);

            b.Draw(Game1.staminaRect,
                new Rectangle(_scrollTrack.X + 4, thumbY, _scrollTrack.Width - 8, thumbHeight),
                WoodBorder * 0.7f);

            var upHovered = _upArrowRect.Contains(Game1.getMouseX(), Game1.getMouseY());
            var downHovered = _downArrowRect.Contains(Game1.getMouseX(), Game1.getMouseY());

            b.Draw(Game1.staminaRect, _upArrowRect, upHovered ? WoodBorder : WoodBorder * 0.5f);
            b.Draw(Game1.staminaRect, _downArrowRect, downHovered ? WoodBorder : WoodBorder * 0.5f);

            var arrowColor = DarkText;
            DrawUpArrow(b, _upArrowRect.Center.X, _upArrowRect.Center.Y, arrowColor);
            DrawDownArrow(b, _downArrowRect.Center.X, _downArrowRect.Center.Y, arrowColor);
        }

        private static void DrawUpArrow(SpriteBatch b, int cx, int cy, Color color)
        {
            b.Draw(Game1.staminaRect, new Rectangle(cx - 4, cy + 2, 8, 2), color);
            b.Draw(Game1.staminaRect, new Rectangle(cx - 3, cy, 6, 2), color);
            b.Draw(Game1.staminaRect, new Rectangle(cx - 2, cy - 2, 4, 2), color);
            b.Draw(Game1.staminaRect, new Rectangle(cx - 1, cy - 4, 2, 2), color);
        }

        private static void DrawDownArrow(SpriteBatch b, int cx, int cy, Color color)
        {
            b.Draw(Game1.staminaRect, new Rectangle(cx - 4, cy - 3, 8, 2), color);
            b.Draw(Game1.staminaRect, new Rectangle(cx - 3, cy - 1, 6, 2), color);
            b.Draw(Game1.staminaRect, new Rectangle(cx - 2, cy + 1, 4, 2), color);
            b.Draw(Game1.staminaRect, new Rectangle(cx - 1, cy + 3, 2, 2), color);
        }

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            base.receiveLeftClick(x, y, playSound);

            if (_upArrowRect.Contains(x, y))
            {
                _scrollOffset = Math.Max(0, _scrollOffset - ScrollStep);
            }
            else if (_downArrowRect.Contains(x, y))
            {
                var maxScroll = Math.Max(0, _contentHeight - _contentArea.Height);
                _scrollOffset = Math.Min(maxScroll, _scrollOffset + ScrollStep);
            }
        }

        public override void receiveScrollWheelAction(int direction)
        {
            base.receiveScrollWheelAction(direction);

            if (direction > 0)
                _scrollOffset = Math.Max(0, _scrollOffset - ScrollStep);
            else
            {
                var maxScroll = Math.Max(0, _contentHeight - _contentArea.Height);
                _scrollOffset = Math.Min(maxScroll, _scrollOffset + ScrollStep);
            }
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
    }
}
