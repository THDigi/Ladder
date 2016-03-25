using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using Sandbox.Common;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Definitions;
using Sandbox.Engine;
using Sandbox.Engine.Physics;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces;
using VRage.Common.Utils;
using VRage.Input;
using VRage.Utils;
using VRageMath;
using VRage;
using Digi.Utils;
using VRageRender;

namespace Digi.Ladder
{
    public class ControlCombination
    {
        public List<object> combination = new List<object>(0);
        private string combinationString = "";
        
        public ControlCombination() { }
        
        public string GetStringCombination()
        {
            return combinationString;
        }
        
        public string GetFriendlyString()
        {
            List<string> combined = new List<string>();
            
            foreach(var input in combination)
            {
                if(input is MyKeys)
                {
                    combined.Add(MyAPIGateway.Input.GetKeyName((MyKeys)input));
                }
                else if(input is MyStringId)
                {
                    var control = MyAPIGateway.Input.GetGameControl((MyStringId)input);
                    
                    if(control.GetMouseControl() != MyMouseButtonsEnum.None)
                    {
                        combined.Add(MyAPIGateway.Input.GetName(control.GetMouseControl()));
                    }
                    else if(control.GetKeyboardControl() != MyKeys.None)
                    {
                        combined.Add(MyAPIGateway.Input.GetKeyName(control.GetKeyboardControl()));
                    }
                    else if(control.GetSecondKeyboardControl() != MyKeys.None)
                    {
                        combined.Add(MyAPIGateway.Input.GetKeyName(control.GetSecondKeyboardControl()));
                    }
                    else
                    {
                        combined.Add(control.GetControlName().ToString());
                    }
                }
                else if(input is MyMouseButtonsEnum)
                {
                    combined.Add(MyAPIGateway.Input.GetName((MyMouseButtonsEnum)input));
                }
                else if(input is MyJoystickAxesEnum)
                {
                    var axis = (MyJoystickAxesEnum)input;
                    
                    if(Settings.xboxCodes.ContainsKey(axis))
                        combined.Add(Settings.xboxCodes[axis].ToString());
                    else
                        combined.Add(MyAPIGateway.Input.GetName(axis));
                }
                else if(input is MyJoystickButtonsEnum)
                {
                    var button = (MyJoystickButtonsEnum)input;
                    
                    if(Settings.xboxCodes.ContainsKey(button))
                        combined.Add(Settings.xboxCodes[button].ToString());
                    else
                        combined.Add(MyAPIGateway.Input.GetName(button));
                }
            }
            
            return String.Join("+", combined);
        }
        
        public bool IsPressed()
        {
            if(combination.Count == 0)
                return false;
            
            foreach(var obj in combination)
            {
                if(obj is MyKeys)
                {
                    if(!MyAPIGateway.Input.IsKeyPress((MyKeys)obj))
                        return false;
                }
                else if(obj is MyStringId)
                {
                    if(!MyAPIGateway.Input.IsGameControlPressed((MyStringId)obj))
                        return false;
                }
                else if(obj is MyMouseButtonsEnum)
                {
                    if(!MyAPIGateway.Input.IsMousePressed((MyMouseButtonsEnum)obj))
                        return false;
                }
                else if(obj is MyJoystickAxesEnum)
                {
                    if(!MyAPIGateway.Input.IsJoystickAxisPressed((MyJoystickAxesEnum)obj))
                        return false;
                }
                else if(obj is MyJoystickButtonsEnum)
                {
                    if(!MyAPIGateway.Input.IsJoystickButtonPressed((MyJoystickButtonsEnum)obj))
                        return false;
                }
            }
            
            return true;
        }
        
        public static ControlCombination FromCombination(string combinationString)
        {
            if(combinationString == null)
                return null;
            
            string[] data = combinationString.ToLower().Split('+');
            
            if(data.Length == 0)
                return null;
            
            var obj = new ControlCombination();
            
            for(int d = 0; d < data.Length; d++)
            {
                var input = data[d].Trim();
                
                if(input.Length == 0)
                {
                    Log.Info("WARNING: Empty key/input: "+input);
                    return null;
                }
                
                if(input.StartsWith("g:"))
                {
                    input = input.Substring(2).Trim();
                    
                    if(input.Length == 0)
                    {
                        Log.Info("WARNING: Empty gamepad input: "+input);
                        return null;
                    }
                    
                    object g;
                    
                    if(Settings.gamepadInputs.TryGetValue(input, out g))
                    {
                        obj.combination.Add(g);
                    }
                    else
                    {
                        Log.Info("WARNING: Gamepad input not found: "+input);
                        return null;
                    }
                }
                else if(input.StartsWith("m:"))
                {
                    input = input.Substring(2).Trim();
                    
                    if(input.Length == 0)
                    {
                        Log.Info("WARNING: Empty gamepad input: "+input);
                        return null;
                    }
                    
                    MyMouseButtonsEnum button;
                    
                    if(MyMouseButtonsEnum.TryParse(input, true, out button) && MyAPIGateway.Input.IsMouseButtonValid(button))
                    {
                        obj.combination.Add(button);
                    }
                    else
                    {
                        Log.Info("WARNING: Mouse input not found: "+input);
                        return null;
                    }
                }
                else if(input.StartsWith("c:"))
                {
                    input = input.Substring(2).Trim();
                    
                    if(input.Length == 0)
                    {
                        Log.Info("WARNING: Empty control: "+input);
                        return null;
                    }
                    
                    if(!Settings.controls.Contains(input))
                    {
                        Log.Info("WARNING: Game control not found: "+input);
                        return null;
                    }
                    
                    obj.combination.Add(MyStringId.GetOrCompute(input.ToUpper()));
                }
                else
                {
                    MyKeys key;
                    
                    if(MyKeys.TryParse(input, true, out key) && Settings.keyNames.Contains(key.ToString().ToLower()))
                    {
                        obj.combination.Add(key);
                    }
                    else
                    {
                        Log.Info("WARNING: Key not found: "+input);
                        return null;
                    }
                }
            }
            
            obj.combinationString = combinationString;
            return obj;
        }
    }

    public class Settings
    {
        private const string FILE = "settings.cfg";
        
        public ControlCombination useLadder1 = ControlCombination.FromCombination("c:use");
        public ControlCombination useLadder2 = null;
        public HashSet<string> onLadderWorlds = new HashSet<string>();
        
        private static char[] CHARS = new char[] { '=' };
        
        public bool firstLoad = false;
        
        public static string[] controls = null;
        public static List<string> keyNames = new List<string>();
        public static List<string> mouseButtonNames = new List<string>();
        public static Dictionary<string, object> gamepadInputs = null;
        public static Dictionary<object, char> xboxCodes = null;
        
        static Settings()
        {
            if(keyNames.Count == 0)
            {
                foreach(MyKeys v in Enum.GetValues(typeof(MyKeys)))
                {
                    if(v == MyKeys.None)
                        continue;
                    
                    if(MyAPIGateway.Input.IsKeyValid(v))
                    {
                        keyNames.Add(v.ToString().ToLower());
                    }
                    else
                    {
                        switch(v)
                        {
                            case MyKeys.Shift:
                            case MyKeys.Alt:
                            case MyKeys.Control:
                                keyNames.Add(v.ToString().ToLower());
                                break;
                        }
                    }
                }
            }
            
            if(mouseButtonNames.Count == 0)
            {
                foreach(MyMouseButtonsEnum v in Enum.GetValues(typeof(MyMouseButtonsEnum)))
                {
                    if(v != MyMouseButtonsEnum.None && MyAPIGateway.Input.IsMouseButtonValid(v))
                    {
                        mouseButtonNames.Add(v.ToString().ToLower());
                    }
                }
            }
            
            if(gamepadInputs == null)
            {
                gamepadInputs = new Dictionary<string, object>()
                {
                    // buttons
                    {"a", MyJoystickButtonsEnum.J01},
                    {"b", MyJoystickButtonsEnum.J02},
                    {"x", MyJoystickButtonsEnum.J03},
                    {"y", MyJoystickButtonsEnum.J04},
                    {"lb", MyJoystickButtonsEnum.J05},
                    {"rb", MyJoystickButtonsEnum.J06},
                    {"back", MyJoystickButtonsEnum.J07},
                    {"start", MyJoystickButtonsEnum.J08},
                    {"ls", MyJoystickButtonsEnum.J09},
                    {"rs", MyJoystickButtonsEnum.J10},
                    {"dpadup", MyJoystickButtonsEnum.JDUp},
                    {"dpaddown", MyJoystickButtonsEnum.JDDown},
                    {"dpadleft", MyJoystickButtonsEnum.JDLeft},
                    {"dpadright", MyJoystickButtonsEnum.JDRight},
                    
                    // axes
                    {"rt", MyJoystickAxesEnum.Zneg},
                    {"lt", MyJoystickAxesEnum.Zpos},
                    {"lsup", MyJoystickAxesEnum.Yneg},
                    {"lsdown", MyJoystickAxesEnum.Ypos},
                    {"lsleft", MyJoystickAxesEnum.Xneg},
                    {"lsright", MyJoystickAxesEnum.Xpos},
                    {"rsup", MyJoystickAxesEnum.RotationYneg},
                    {"rsdown", MyJoystickAxesEnum.RotationYpos},
                    {"rsleft", MyJoystickAxesEnum.RotationXneg},
                    {"rsright", MyJoystickAxesEnum.RotationXpos},
                    
                    // unknown buttons
                    {"j11", MyJoystickButtonsEnum.J11},
                    {"j12", MyJoystickButtonsEnum.J12},
                    {"j13", MyJoystickButtonsEnum.J13},
                    {"j14", MyJoystickButtonsEnum.J14},
                    {"j15", MyJoystickButtonsEnum.J15},
                    {"j16", MyJoystickButtonsEnum.J16},
                    
                    // unknown axes
                    {"rotzneg", MyJoystickAxesEnum.RotationZneg},
                    {"rotzpos", MyJoystickAxesEnum.RotationZpos},
                    {"slider1neg", MyJoystickAxesEnum.Slider1neg},
                    {"slider1pos", MyJoystickAxesEnum.Slider1pos},
                    {"slider2neg", MyJoystickAxesEnum.Slider2neg},
                    {"slider2pos", MyJoystickAxesEnum.Slider2pos},
                };
            }
            
            if(xboxCodes == null)
            {
                xboxCodes = new Dictionary<object, char>
                {
                    // buttons
                    { MyJoystickButtonsEnum.J01, '\xe001' },
                    { MyJoystickButtonsEnum.J02, '\xe003' },
                    { MyJoystickButtonsEnum.J03, '\xe002' },
                    { MyJoystickButtonsEnum.J04, '\xe004' },
                    { MyJoystickButtonsEnum.J05, '\xe005' },
                    { MyJoystickButtonsEnum.J06, '\xe006' },
                    { MyJoystickButtonsEnum.J07, '\xe00d' },
                    { MyJoystickButtonsEnum.J08, '\xe00e' },
                    { MyJoystickButtonsEnum.J09, '\xe00b' },
                    { MyJoystickButtonsEnum.J10, '\xe00c' },
                    { MyJoystickButtonsEnum.JDLeft, '\xe010' },
                    { MyJoystickButtonsEnum.JDUp, '\xe011' },
                    { MyJoystickButtonsEnum.JDRight, '\xe012' },
                    { MyJoystickButtonsEnum.JDDown, '\xe013' },
                    
                    // axes
                    { MyJoystickAxesEnum.Xneg, '\xe016' },
                    { MyJoystickAxesEnum.Xpos, '\xe015' },
                    { MyJoystickAxesEnum.Ypos, '\xe014' },
                    { MyJoystickAxesEnum.Yneg, '\xe017' },
                    { MyJoystickAxesEnum.RotationXneg, '\xe020' },
                    { MyJoystickAxesEnum.RotationXpos, '\xe019' },
                    { MyJoystickAxesEnum.RotationYneg, '\xe021' },
                    { MyJoystickAxesEnum.RotationYpos, '\xe018' },
                    { MyJoystickAxesEnum.Zneg, '\xe007' },
                    { MyJoystickAxesEnum.Zpos, '\xe008' },
                };
            }
            
            if(controls == null)
            {
                controls = new string[]
                {
                    "forward",
                    "backward",
                    "strafe_left",
                    "strafe_right",
                    "roll_left",
                    "roll_right",
                    "sprint",
                    "primary_tool_action",
                    "secondary_tool_action",
                    "jump",
                    "crouch",
                    "switch_walk",
                    "use",
                    "terminal",
                    "help_screen",
                    "control_menu",
                    "factions_menu",
                    "rotation_left",
                    "rotation_right",
                    "rotation_up",
                    "rotation_down",
                    "headlights",
                    "screenshot",
                    "lookaround",
                    "switch_left",
                    "switch_right",
                    "cube_color_change",
                    "toggle_reactors",
                    "build_screen",
                    "cube_rotate_vertical_positive",
                    "cube_rotate_vertical_negative",
                    "cube_rotate_horisontal_positive",
                    "cube_rotate_horisontal_negative",
                    "cube_rotate_roll_positive",
                    "cube_rotate_roll_negative",
                    "symmetry_switch",
                    "use_symmetry",
                    "switch_compound",
                    "switch_building_mode",
                    "voxel_hand_settings",
                    "mission_settings",
                    "cockpit_build_mode",
                    "slot1",
                    "slot2",
                    "slot3",
                    "slot4",
                    "slot5",
                    "slot6",
                    "slot7",
                    "slot8",
                    "slot9",
                    "slot0",
                    "toolbar_up",
                    "toolbar_down",
                    "toolbar_next_item",
                    "toolbar_prev_item",
                    "toggle_hud",
                    "damping",
                    "thrusts",
                    "camera_mode",
                    "broadcasting",
                    "helmet",
                    "chat_screen",
                    "console",
                    "suicide",
                    "landing_gear",
                    "inventory",
                    "pause_game",
                    "spectator_none",
                    "spectator_delta",
                    "spectator_free",
                    "spectator_static",
                    "station_rotation",
                    "voice_chat",
                    "voxel_paint",
                    "build_mode",
                    "next_block_stage",
                    "prev_block_stage",
                    "move_closer",
                    "move_further",
                    "primary_build_action",
                    "secondary_build_action",
                    "copy_paste_action",
                };
            }
        }
        
        public Settings()
        {
            // load the settings if they exist
            if(!Load())
            {
                firstLoad = true; // config didn't exist, assume it's the first time the mod is loaded
            }
            
            Save(); // refresh config in case of any missing or extra settings
        }
        
        public bool Load()
        {
            try
            {
                if(MyAPIGateway.Utilities.FileExistsInLocalStorage(FILE, typeof(Settings)))
                {
                    var file = MyAPIGateway.Utilities.ReadFileInLocalStorage(FILE, typeof(Settings));
                    ReadSettings(file);
                    file.Close();
                    return true;
                }
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
            
            return false;
        }
        
        private void ReadSettings(TextReader file)
        {
            try
            {
                string line;
                string[] args;
                int i;
                
                while((line = file.ReadLine()) != null)
                {
                    if(line.Length == 0)
                        continue;
                    
                    i = line.IndexOf("//");
                    
                    if(i > -1)
                        line = (i == 0 ? "" : line.Substring(0, i));
                    
                    if(line.Length == 0)
                        continue;
                    
                    args = line.Split(CHARS, 2);
                    
                    if(args.Length != 2)
                    {
                        Log.Error("Unknown "+FILE+" line: "+line+"\nMaybe is missing the '=' ?");
                        continue;
                    }
                    
                    args[0] = args[0].Trim().ToLower();
                    args[1] = args[1].Trim().ToLower();
                    
                    switch(args[0])
                    {
                        case "useladder1":
                        case "useladder2":
                            if(args[1].Length == 0)
                                continue;
                            var obj = ControlCombination.FromCombination(args[1]);
                            if(obj != null)
                            {
                                if(args[0] == "useladder1")
                                    useLadder1 = obj;
                                else
                                    useLadder2 = obj;
                            }
                            else
                                Log.Error("Invalid "+args[0]+" value: " + args[1]);
                            continue;
                        case "onladderworlds":
                            onLadderWorlds = new HashSet<string>(args[1].Split('/'));
                            continue;
                    }
                }
                
                Log.Info("Loaded settings:\n" + GetSettingsString(false));
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }
        
        public void Save()
        {
            try
            {
                var file = MyAPIGateway.Utilities.WriteFileInLocalStorage(FILE, typeof(Settings));
                file.Write(GetSettingsString(true));
                file.Flush();
                file.Close();
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }
        
        public string GetSettingsString(bool comments)
        {
            var str = new StringBuilder();
            
            if(comments)
            {
                str.AppendLine("// Ladder mod config; this file gets automatically overwritten after being loaded so don't leave custom comments.");
                str.AppendLine("// You can reload this while the game is running by typing in chat: /ladder reload");
                str.AppendLine("// Lines starting with // are comments. All values are case insensitive unless otherwise specified.");
                str.AppendLine();
            }
            
            if(comments)
            {
                str.AppendLine();
                str.AppendLine("// Key/mouse/gamepad combination to use ladders.");
                str.AppendLine("// Use + to combine more than one. For gamepad add g: prefix, for mouse add m: prefix and for game controls add c: prefix.");
                str.AppendLine("// All keys, mouse buttons, gamepad buttons/axes and control names are at the bottom of this file.");
            }
            str.Append("useladder1=").Append(useLadder1 == null ? "" : useLadder1.GetStringCombination()).AppendLine(comments ? " // Default: c:use" : "");
            str.Append("useladder2=").Append(useLadder2 == null ? "" : useLadder2.GetStringCombination()).AppendLine(comments ? " // Default:" : "");
            
            if(comments)
            {
                str.AppendLine();
                str.AppendLine("// List of inputs, generated from game data.");
                str.AppendLine("// Key names: "+String.Join(", ", keyNames));
                str.AppendLine("// Mouse button names: "+String.Join(", ", mouseButtonNames));
                str.AppendLine("// Gamepad button/axes names: "+String.Join(", ", gamepadInputs.Keys));
                str.AppendLine("// Control names: "+String.Join(", ", controls));
            }
            
            if(comments)
                str.AppendLine().AppendLine().AppendLine();
            
            str.Append("onladderworlds=").Append(String.Join("/", onLadderWorlds)).AppendLine(comments ? " // DO NOT EDIT, list of worlds where you saved while on a ladder, separated by /" : "");
            
            return str.ToString();
        }
        
        public void Close()
        {
        }
    }
}