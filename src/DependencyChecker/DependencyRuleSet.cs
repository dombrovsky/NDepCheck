﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DependencyChecker {
    public class DependencyRuleSet {
        private const string LEFT_PARAM = "\\L";
        private const string MACRO_DEFINE = ":=";
        private const string MACRO_END = "=:";
        private const string RIGHT_PARAM = "\\R";

        internal const string MAYUSE = "--->";
        internal const string MAYUSE_WITH_WARNING = "---?";
        internal const string MUSTNOTUSE = "---!";

        internal const string GRAPHIT = "%";

        /// <summary>
        /// Constant for one-line defines.
        /// </summary>
        public const string DEFINE = ":=";

        private static readonly List<string> _reservedNames = new List<string>();

        static DependencyRuleSet() {
            _reservedNames.Add(DEFINE.Trim());
            _reservedNames.Add(MAYUSE.Trim());
            _reservedNames.Add(MUSTNOTUSE.Trim());
            _reservedNames.Add(MAYUSE_WITH_WARNING.Trim());
            _reservedNames.Add(GRAPHIT.Trim());
            _reservedNames.Add(MACRO_DEFINE.Trim());
            _reservedNames.Add(MACRO_END.Trim());
            _reservedNames.Add(LEFT_PARAM.Trim());
            _reservedNames.Add(RIGHT_PARAM.Trim());
        }


        class LengthComparer : IComparer<string> {
            public int Compare(string s1, string s2) {
                return s2.Length - s1.Length;
            }
        }

        private static readonly IDictionary<string, DependencyRuleSet> _fullFilename2RulesetCache = new Dictionary<string, DependencyRuleSet>();

        private readonly List<DependencyRule> _allowed = new List<DependencyRule>();
        private readonly List<DependencyRule> _questionable = new List<DependencyRule>();
        private readonly List<DependencyRule> _forbidden = new List<DependencyRule>();
        private readonly List<DependencyRuleRepresentation> _representations = new List<DependencyRuleRepresentation>();

        private readonly List<GraphAbstraction> _graphAbstractions = new List<GraphAbstraction>();

        private readonly List<DependencyRuleSet> _includedRuleSets = new List<DependencyRuleSet>();
        private readonly Dictionary<string, Macro> _macros = new Dictionary<string, Macro>();

        private readonly SortedDictionary<string, string> _defines = new SortedDictionary<string, string>(new LengthComparer());
        private readonly bool _verbose;

        /// <summary>
        /// Constructor for test cases.
        /// </summary>
        public DependencyRuleSet(bool verbose) {
            _verbose = verbose;
        }

        private DependencyRuleSet(string fullRuleFilename, bool verbose) {
            _verbose = verbose;
            if (!LoadRules(fullRuleFilename, verbose)) {
                throw new ApplicationException("...");
            }
        }

        public static DependencyRuleSet Create(DirectoryInfo relativeRoot, string rulefilename, bool verbose) {
            string fullRuleFilename = Path.Combine(relativeRoot.FullName, rulefilename);
            if (_fullFilename2RulesetCache.ContainsKey(fullRuleFilename)) {
                return _fullFilename2RulesetCache[fullRuleFilename];
            } else {
                long start = Environment.TickCount;
                try {
                    return new DependencyRuleSet(fullRuleFilename, verbose);
                } catch (FileNotFoundException) {
                    DependencyCheckerMain.WriteError("File " + fullRuleFilename + " not found");
                    return null;
                } finally {
                    DependencyCheckerMain.WriteInfo("Completed reading " + fullRuleFilename + " in " + (Environment.TickCount - start) + " ms");
                }
            }
        }

        #region Loading

        /// <summary>
        /// Load a rule file.
        /// </summary>
        private bool LoadRules(string fullRuleFilename, bool verbose) {
            using (TextReader tr = new StreamReader(fullRuleFilename, Encoding.Default)) {
                return ProcessText(fullRuleFilename, 0, tr, LEFT_PARAM, RIGHT_PARAM, verbose);
            }
        }

        private bool ProcessText(string fullRuleFilename, uint startLineNo, TextReader tr, string leftParam,
                                 string rightParam, bool verbose) {
            uint lineNo = startLineNo;
            bool textIsOk = true;
            for (; ; ) {
                string line = tr.ReadLine();

                if (line == null) {
                    break;
                }

                line = line.Trim().Replace(LEFT_PARAM, leftParam).Replace(RIGHT_PARAM, rightParam);
                lineNo++;

                if (line == "" || line.StartsWith("#") || line.StartsWith("//")) {
                    // ignore;
                } else if (line.StartsWith("+")) {
                    string includeFilename = line.Substring(1).Trim();
                    _includedRuleSets.Add(Create(new FileInfo(fullRuleFilename).Directory, includeFilename, verbose));
                } else if (ProcessMacroIfFound(line, verbose)) {
                    // macro is already processed as side effect in ProcessMacroIfFound()
                } else if (line.Contains(MAYUSE) || line.Contains(MUSTNOTUSE) ||
                           line.Contains(MAYUSE_WITH_WARNING)) {
                    AddDependencyRules(fullRuleFilename, lineNo, line);
                } else if (line.StartsWith(GRAPHIT)) {
                    AddGraphAbstractions(fullRuleFilename, lineNo, line);
                } else if (line.EndsWith(MACRO_DEFINE)) {
                    string macroName = line.Substring(0, line.Length - MACRO_DEFINE.Length).Trim();
                    if (!CheckDefinedName(macroName, fullRuleFilename, lineNo)) {
                        textIsOk = false;
                    }
                    string macroText = "";
                    uint macroStartLineNo = lineNo;
                    for (; ; ) {
                        line = tr.ReadLine();
                        lineNo++;
                        if (line == null) {
                            DependencyCheckerMain.WriteError(fullRuleFilename + ": Missing " + MACRO_END + " at end", fullRuleFilename, lineNo, 0, 0,
                                       0);
                            textIsOk = false;
                            break;
                        }
                        line = line.Trim();
                        if (line == MACRO_END) {
                            _macros[macroName] = new Macro(macroText, fullRuleFilename, macroStartLineNo);
                            break;
                        } else {
                            macroText += line + "\n";
                        }
                    }
                } else if (line.Contains(DEFINE)) {
                    AddDefine(fullRuleFilename, lineNo, line);
                    AddDefine(fullRuleFilename, lineNo, line);
                } else {
                    DependencyCheckerMain.WriteError(fullRuleFilename + ": Cannot parse line " + lineNo + ": " + line, fullRuleFilename, lineNo, 0, 0,
                               0);
                    textIsOk = false;
                }
            }
            return textIsOk;
        }

        private bool CheckDefinedName(string macroName, string ruleFileName, uint lineNo) {
            if (macroName.Contains(" ")) {
                DependencyCheckerMain.WriteError(
                    ruleFileName + ", line " + lineNo + ": Macro name must not contain white space: " + macroName,
                    ruleFileName,
                    lineNo, 0, 0, 0);
                return false;
            } else {
                string compactedName = CompactedName(macroName);
                foreach (string reservedName in _reservedNames) {
                    if (compactedName == CompactedName(reservedName)) {
                        DependencyCheckerMain.WriteError(
                            ruleFileName + ", line " + lineNo + ": Macro name " + macroName +
                            " is too similar to predefined name " +
                            reservedName, ruleFileName, lineNo, 0, 0, 0);
                        return false;
                    }
                }
                foreach (string definedMacroName in _macros.Keys) {
                    if (compactedName == CompactedName(definedMacroName)) {
                        DependencyCheckerMain.WriteError(
                            ruleFileName + ", line " + lineNo + ": Macro name " + macroName +
                            " is too similar to already defined name " +
                            definedMacroName, ruleFileName, lineNo, 0, 0, 0);
                        return false;
                    }
                }
                return true;
            }
        }

        private static string CompactedName(IEnumerable<char> name) {
            // Replace sequences of equal chars with a single char here
            // Reason: ===> can be confused with ====> - so I allow only
            // one of these to be defined.
            string result = "";
            char previousC = ' ';
            foreach (char c in name) {
                if (c != previousC) {
                    result += c;
                    previousC = c;
                }
            }
            return result.ToUpperInvariant();
        }

        private bool ProcessMacroIfFound(string line, bool verbose) {
            string foundMacroName;
            foreach (string macroName in _macros.Keys) {
                if (line.Contains(macroName)) {
                    foundMacroName = macroName;
                    goto PROCESS_MACRO;
                }
            }
            return false;

        PROCESS_MACRO:
            Macro macro = _macros[foundMacroName];
            int macroPos = line.IndexOf(foundMacroName);
            string leftParam = line.Substring(0, macroPos).Trim();
            string rightParam = line.Substring(macroPos + foundMacroName.Length).Trim();
            ProcessText(macro.RuleFileName, macro.StartLineNo, new StringReader(macro.MacroText), leftParam, rightParam, verbose);
            return true;
        }

        #endregion Loading

        #region Nested type: Macro

        private class Macro {
            public readonly string MacroText;
            public readonly string RuleFileName;
            public readonly uint StartLineNo;

            internal Macro(string macroText, string ruleFileName, uint startlineNo) {
                MacroText = macroText;
                RuleFileName = ruleFileName;
                StartLineNo = startlineNo;
            }
        }

        #endregion
        /// <summary>
        /// Add one-line macro definition.
        /// </summary>
        /// <param name="ruleFileName"></param>
        /// <param name="lineNo"></param>
        /// <param name="line"></param>
        private void AddDefine(string ruleFileName, uint lineNo, string line) {
            int i = line.IndexOf(DEFINE);
            string key = line.Substring(0, i).Trim();
            string value = line.Substring(i + DEFINE.Length).Trim();
            if (key != key.ToUpper()) {
                throw new ApplicationException("Key in " + ruleFileName + ":" + lineNo + " is not uppercase-only");
            }
            _defines[key] = ExpandDefines(value);
        }

        /// <summary>
        /// Expand first found macro in s.
        /// </summary>
        /// <param name="s"></param>
        /// <returns></returns>
        private string ExpandDefines(string s) {
            // Debug.WriteLine("--------");
            foreach (string key in _defines.Keys) {
                // Debug.WriteLine(key);
                if (s.Contains(key)) {
                    return s.Replace(key, _defines[key]);
                }
            }
            return s;
        }

        /// <summary>
        /// Add one or more <c>DependencyRules</c>s from a single input
        /// line.
        /// public for testability.
        /// </summary>
        public void AddDependencyRules(string ruleFileName, uint lineNo, string line) {
            if (line.Contains(MAYUSE)) {
                foreach (var rule in CreateDependencyRules(ruleFileName, lineNo, line, MAYUSE, false)) {
                    Add(_allowed, rule);
                }
            } else if (line.Contains(MAYUSE_WITH_WARNING)) {
                foreach (var rule in CreateDependencyRules(ruleFileName, lineNo, line, MAYUSE_WITH_WARNING, true)) {
                    Add(_questionable, rule);
                }
            } else if (line.Contains(MUSTNOTUSE)) {
                foreach (var rule in CreateDependencyRules(ruleFileName, lineNo, line, MUSTNOTUSE, false)) {
                    Add(_forbidden, rule);
                }
            } else {
                throw new ApplicationException("Unexpected rule at " + ruleFileName + ":" + lineNo);
            }
        }

        private static void Add(List<DependencyRule> ruleList, DependencyRule rule) {
            if (!ruleList.Exists(r => r.IsSameAs(rule))) {
                ruleList.Add(rule);
            }
        }

        private IEnumerable<DependencyRule> CreateDependencyRules(string ruleFileName, uint lineNo, string line, string sep, bool questionableRule) {
            DependencyRuleRepresentation rep = new DependencyRuleRepresentation(ruleFileName, lineNo, line, questionableRule);
            _representations.Add(rep);
            int i = line.IndexOf(sep);
            string usingPattern = ExpandDefines(line.Substring(0, i).Trim());
            string usedPattern = ExpandDefines(line.Substring(i + sep.Length).Trim());
            List<DependencyRule> deps = DependencyRule.CreateDependencyRules(usingPattern, usedPattern, rep);

            if (_verbose) {
                DependencyCheckerMain.WriteInfo("Rules used for checking " + line + " (" + ruleFileName + ":" + lineNo + ")");
                foreach (DependencyRule d in deps) {
                    DependencyCheckerMain.WriteInfo("  " + d);
                }
            }
            return deps;
        }

        /// <summary>
        /// Add one or more <c>GraphAbstraction</c>s from a single input
        /// line (with leading %).
        /// public for testability.
        /// </summary>
        public void AddGraphAbstractions(string ruleFileName, uint lineNo, string line) {
            line = ExpandDefines(line.Substring(GRAPHIT.Length).Trim());
            List<GraphAbstraction> a = GraphAbstraction.CreateGraphAbstractions(line);
            _graphAbstractions.AddRange(a);
            if (_verbose) {
                DependencyCheckerMain.WriteInfo("Reg.exps used for drawing " + line + " (" + ruleFileName + ":" + lineNo + ")");
                foreach (GraphAbstraction ga in a) {
                    DependencyCheckerMain.WriteInfo(ga.ToString());
                }
            }
        }

        public static DependencyRuleSet Load(string dependencyFilename, List<DirectoryOption> directories, bool verbose) {
            foreach (var d in directories) {
                string[] f = Directory.GetFiles(d.Path, dependencyFilename, d.SearchOption);
                if (f.Length == 0) {
                    // continue
                } else if (f.Length == 1) {
                    return Create(new DirectoryInfo("."), f[0], verbose);
                } else {
                    throw new FileLoadException(d.Path + " contains two files named " + dependencyFilename);
                }
            }
            return null; // if nothing found
        }

        internal void ExtractGraphAbstractions(List<GraphAbstraction> graphAbstractions) {
            ExtractGraphAbstractions(graphAbstractions, new List<DependencyRuleSet>());
        }

        private void ExtractGraphAbstractions(List<GraphAbstraction> graphAbstractions, List<DependencyRuleSet> visited) {
            if (visited.Contains(this)) {
                return;
            }
            visited.Add(this);
            graphAbstractions.AddRange(_graphAbstractions);
            foreach (var includedRuleSet in _includedRuleSets) {
                includedRuleSet .ExtractGraphAbstractions(graphAbstractions, visited);                
            }
        }

        internal void ExtractDependencyRules(List<DependencyRule> allowed, List<DependencyRule> questionable, List<DependencyRule> forbidden) {
            ExtractDependencyRules(allowed, questionable, forbidden, new List<DependencyRuleSet>());
        }

        private void ExtractDependencyRules(List<DependencyRule> allowed, List<DependencyRule> questionable, List<DependencyRule> forbidden, List<DependencyRuleSet> visited) {
            if (visited.Contains(this)) {
                return;
            }
            visited.Add(this);
            allowed.AddRange(_allowed);
            questionable.AddRange(_questionable);
            forbidden.AddRange(_forbidden);
            foreach (var includedRuleSet in _includedRuleSets) {
                includedRuleSet.ExtractDependencyRules(allowed, questionable, forbidden, visited);
            }
        }
    }
}
