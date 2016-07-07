using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using Sandbox.ModAPI;
using Digi.Utils;

namespace Digi.Ladder
{
    public class Settings
    {
        private const string FILE = "settings.cfg";
        
        public ControlCombination useLadder1 = ControlCombination.CreateFrom(InputHandler.CONTROL_PREFIX+"use");
        public ControlCombination useLadder2 = ControlCombination.CreateFrom(InputHandler.GAMEPAD_PREFIX+"x");
        public bool relativeControls = true;
        public bool clientPrediction = true; // TODO temporary?
        
        private static char[] CHARS = new char[] { '=' };
        
        public bool firstLoad = false;
        
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
                bool b;
                
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
                        case "useladderinput1":
                        case "useladderinput2":
                            if(args[1].Length == 0)
                                continue;
                            var obj = ControlCombination.CreateFrom(args[1]);
                            if(obj != null)
                            {
                                if(args[0].EndsWith("1"))
                                    useLadder1 = obj;
                                else
                                    useLadder2 = obj;
                            }
                            else
                                Log.Error("Invalid "+args[0]+" value: " + args[1]);
                            continue;
                        case "relativecontrols":
                            if(bool.TryParse(args[1], out b))
                                relativeControls = b;
                            else
                                Log.Error("Invalid "+args[0]+" value: " + args[1]);
                            continue;
                        case "clientprediction":
                            if(bool.TryParse(args[1], out b))
                                clientPrediction = b;
                            else
                                Log.Error("Invalid "+args[0]+" value: " + args[1]);
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
                str.AppendLine("// Toggles wether ladder control is relative to the camera.");
                str.AppendLine("// For example: looking down will cause W to climb and looking forward or up will cause W to climb up, similarily with A, D and S.");
                str.AppendLine("// Setting this to false will cause W/S to always climb up/down and A/D to always move left/right regardless of where you're looking.");
                str.AppendLine("// Default: true");
            }
            str.Append("RelativeControls=").Append(relativeControls ? "true" : "false").AppendLine();
            
            if(comments)
            {
                str.AppendLine();
                str.AppendLine("// Key/mouse/gamepad combination to use ladders.");
                str.AppendLine("// Separate multiple keys/buttons/controls with spaces. For gamepad add "+InputHandler.GAMEPAD_PREFIX+" prefix, for mouse add "+InputHandler.MOUSE_PREFIX+" prefix and for game controls add "+InputHandler.CONTROL_PREFIX+" prefix.");
                str.AppendLine("// All keys, mouse buttons, gamepad buttons/axes and control names are at the bottom of this file.");
            }
            str.Append("UseLadderInput1=").Append(useLadder1 == null ? "" : useLadder1.GetStringCombination()).AppendLine(comments ? " // Default: "+InputHandler.CONTROL_PREFIX+"use" : "");
            str.Append("UseLadderInput2=").Append(useLadder2 == null ? "" : useLadder2.GetStringCombination()).AppendLine(comments ? " // Default: "+InputHandler.GAMEPAD_PREFIX+"x" : "");
            
            if(comments)
            {
                str.AppendLine();
                str.AppendLine("// List of inputs, generated from game data.");
                
                str.Append("// Key names: ");
                foreach(var kv in InputHandler.inputs)
                {
                    if(kv.Key.StartsWith(InputHandler.MOUSE_PREFIX) || kv.Key.StartsWith(InputHandler.GAMEPAD_PREFIX) || kv.Key.StartsWith(InputHandler.CONTROL_PREFIX))
                        continue;
                    
                    str.Append(kv.Key).Append(", ");
                }
                str.AppendLine();
                
                str.Append("// Mouse button names: ");
                foreach(var kv in InputHandler.inputs)
                {
                    if(kv.Key.StartsWith(InputHandler.MOUSE_PREFIX))
                    {
                        str.Append(kv.Key).Append(", ");
                    }
                }
                str.AppendLine();
                
                str.Append("// Gamepad button/axes names: ");
                foreach(var kv in InputHandler.inputs)
                {
                    if(kv.Key.StartsWith(InputHandler.GAMEPAD_PREFIX))
                    {
                        str.Append(kv.Key).Append(", ");
                    }
                }
                str.AppendLine();
                
                str.Append("// Control names: ");
                foreach(var kv in InputHandler.inputs)
                {
                    if(kv.Key.StartsWith(InputHandler.CONTROL_PREFIX))
                    {
                        str.Append(kv.Key).Append(", ");
                    }
                }
                str.AppendLine();
            }
            
            if(comments)
            {
                str.AppendLine().AppendLine();
                str.AppendLine("// Testing features:");
            }
            
            str.Append("ClientPrediction=").Append(clientPrediction ? "true" : "false").AppendLine(comments ? " // toggle client movement prediction, only affects you and only if you're a client in a MP server, default: true" : "");
            
            return str.ToString();
        }
        
        public void Close()
        {
        }
    }
}