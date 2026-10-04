using System;

namespace MagicKeyboardBridge
{
    public static class A1644Mapper
    {
        // USB A1644 report 1: report ID, modifiers, reserved, six usages, Apple flags.
        // Output is an eight-byte standard keyboard report, without its report ID.
        // Optional Option/Command swap applies only to this Apple keyboard.
        public static byte[] Translate(byte[] input, bool swapOptionCommand = false)
        {
            if (input == null || input.Length != 10 || input[0] != 1)
                throw new ArgumentException("Expected the A1644 ten-byte keyboard report 1.");
            var output = new byte[8];
            bool functionLayer = (input[1] & 1) != 0;
            bool physicalFn = (input[9] & 2) != 0;
            output[0] = (byte)((input[1] & 0xFE) | (physicalFn ? 1 : 0));
            if (functionLayer && physicalFn)
                output[0] = (byte)((output[0] & 0xFE) | 0x10);
            if (swapOptionCommand) output[0] = SwapOptionAndCommand(output[0]);
            // Preserve HID error/rollover arrays; deduplication would change their meaning.
            if (Array.Exists(input[3..9], usage => usage >= 1 && usage <= 3))
            {
                Array.Copy(input, 3, output, 2, 6);
                return output;
            }
            int cursor = 2;
            for (int i = 3; i <= 8; i++)
            {
                byte usage = input[i];
                if (functionLayer)
                {
                    switch (usage)
                    {
                        case 0x4F: usage = 0x4D; break; // right -> End
                        case 0x50: usage = 0x4A; break; // left -> Home
                        case 0x51: usage = 0x4E; break; // down -> Page Down
                        case 0x52: usage = 0x4B; break; // up -> Page Up
                        case 0x2A: usage = 0x4C; break; // backspace -> Delete
                        case 0x28: usage = 0x49; break; // Enter -> Insert
                        case 0x13: usage = 0x46; break; // P -> Print Screen
                        case 0x16: usage = 0x47; break; // S -> Scroll Lock
                        case 0x05: usage = 0x48; break; // B -> Pause
                        default:
                            if (usage >= 0x3A && usage <= 0x45) usage = (byte)(usage + 0x2E); // F1..12 -> F13..24
                            break;
                    }
                }
                if (usage == 0) continue;
                bool duplicate = false;
                for (int j = 2; j < cursor; j++) if (output[j] == usage) duplicate = true;
                if (!duplicate && cursor < output.Length) output[cursor++] = usage;
            }
            if ((input[9] & 1) != 0)
            {
                bool present = false;
                for (int i = 2; i < output.Length; i++) if (output[i] == 0x4C) present = true;
                if (!present && cursor < output.Length) output[cursor] = 0x4C;
            }
            return output;
        }

        public static byte SwapOptionAndCommand(byte modifiers)
        {
            return (byte)((modifiers & 0x33) | ((modifiers & 0x44) << 1) | ((modifiers & 0x88) >> 1));
        }

        public static int SelfTest()
        {
            int count = 0;
            Action<bool, string> check = (ok, name) => { if (!ok) throw new Exception("Mapping test failed: " + name); count++; };
            // Exhaustively check all eight hardware modifiers combined with Fn/Control states.
            for (int modifiers = 0; modifiers < 256; modifiers++)
                for (int fn = 0; fn < 2; fn++)
                {
                    var input = new byte[10]; input[0] = 1; input[1] = (byte)modifiers; input[9] = (byte)(fn * 2);
                    var output = Translate(input);
                    int expected = (modifiers & 0xFE) | fn;
                    if ((modifiers & 1) != 0 && fn != 0) expected = (expected & 0xFE) | 0x10;
                    check(output[0] == expected, "modifier combination");
                }
            var held = new byte[] { 1, 2, 0, 0x04, 0, 0, 0, 0, 0, 2 };
            check(Translate(held)[0] == 3 && Translate(held)[2] == 0x04, "Fn+Shift+A");
            held[9] = 0;
            check(Translate(held)[0] == 2 && Translate(held)[2] == 0x04, "Fn released before A");
            var navigation = new byte[] { 1, 1, 0, 0x50, 0x51, 0x52, 0x4F, 0x28, 0x2A, 0 };
            var mapped = Translate(navigation);
            check(BitConverter.ToString(mapped) == "00-00-4A-4E-4B-4D-49-4C", "six simultaneous navigation keys");
            var allUp = new byte[10]; allUp[0] = 1;
            check(BitConverter.ToString(Translate(allUp)) == "00-00-00-00-00-00-00-00", "release all keys");
            bool rejected = false;
            try { Translate(new byte[65]); } catch (ArgumentException) { rejected = true; }
            check(rejected, "reject unrelated report");
            return count;
        }
    }
}
