using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Flinch.Api.Models;
using Flinch.Core;
using Flinch.Core.Contracts;

namespace Flinch.Api.Services
{
    /// <summary>
    /// Offline-first AI provider with smart intent routing:
    /// 1. Only refers to screen if explicitly requested ("on the screen", "on my display", or attached target).
    /// 2. Integrates Exa Search for real-time web information.
    /// 3. Remembers past conversations (RAG context).
    /// 4. Asks clarifying questions if problem is ambiguous ("What problem?").
    /// </summary>
    public sealed class FakeAiProvider : IAiProvider
    {
        private readonly Flinch.Core.MathSolverService _mathSolver;
        private readonly ExaSearchService? _exaSearch;

        public string ProviderName => "OfflineFakeProvider";

        public FakeAiProvider(Flinch.Core.MathSolverService? mathSolver = null, ExaSearchService? exaSearch = null)
        {
            _mathSolver = mathSolver ?? new Flinch.Core.MathSolverService();
            _exaSearch = exaSearch;
        }

        public async IAsyncEnumerable<AnswerStreamChunk> GenerateAnswerStreamAsync(
            AnswerRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var planId = "plan-" + Guid.NewGuid().ToString("N")[..8];

            // 1. Plan Started Chunk
            yield return new AnswerStreamChunk
            {
                Type = StreamChunkType.PlanStarted,
                PlanId = planId,
                IsVerified = true
            };

            await Task.Delay(20, cancellationToken);

            string queryTrimmed = request.Query.Trim();
            string queryLower = queryTrimmed.ToLowerInvariant();
            bool hasScreen = request.Screen != null;

            // Invariant: Only analyze screen when explicitly requested by user prompt or explicit flag
            bool isScreenExplicitlyRequested = request.ExplicitScreenRequested ||
                                               queryLower.Contains("on the screen") ||
                                               queryLower.Contains("on my screen") ||
                                               queryLower.Contains("this window") ||
                                               queryLower.Contains("what's on screen") ||
                                               queryLower.Contains("what you see");

            // RAG: Inspect past conversation history for previously mentioned problems/equations
            string? recentProblemFromHistory = null;
            if (request.ConversationHistory != null && request.ConversationHistory.Count > 0)
            {
                var pastUserMsgs = request.ConversationHistory
                    .Where(m => m.Role.Equals("user", StringComparison.OrdinalIgnoreCase))
                    .Reverse()
                    .ToList();

                foreach (var msg in pastUserMsgs)
                {
                    if (msg.Content.Contains('=') || msg.Content.Contains('+') || msg.Content.ToLowerInvariant().Contains("solve"))
                    {
                        recentProblemFromHistory = msg.Content;
                        break;
                    }
                }
            }

            // =========================================================================
            // SCENARIO 1: Ambiguous "Answer the problem" without screen or past context
            // =========================================================================
            if ((queryLower == "answer the problem" || queryLower == "solve the problem" || queryLower == "fix the problem") &&
                !isScreenExplicitlyRequested && string.IsNullOrEmpty(recentProblemFromHistory))
            {
                yield return new AnswerStreamChunk
                {
                    Type = StreamChunkType.Segment,
                    PlanId = planId,
                    Segment = new ExplanationSegment
                    {
                        Id = "seg-1",
                        VisibleText = "What problem are you looking to answer? You can type a math equation, ask any question, or say \"on the screen\" to have me examine your active window.",
                        SpokenText = "What problem are you looking to answer? You can ask a question, type a math equation, or say 'on the screen' to have me check your window.",
                        VisualCueIds = new List<string>()
                    }
                };
            }
            // =========================================================================
            // SCENARIO 2: Ambiguous query, but RAG remembers problem from past turns
            // =========================================================================
            else if ((queryLower.StartsWith("answer") || queryLower.StartsWith("solve") || queryLower.StartsWith("fix")) &&
                     !isScreenExplicitlyRequested && !string.IsNullOrEmpty(recentProblemFromHistory))
            {
                yield return new AnswerStreamChunk
                {
                    Type = StreamChunkType.Segment,
                    PlanId = planId,
                    Segment = new ExplanationSegment
                    {
                        Id = "seg-1",
                        VisibleText = $"Referring to our previous conversation about \"{recentProblemFromHistory}\": here is the step-by-step resolution.",
                        SpokenText = $"Referring to the problem you mentioned earlier: {recentProblemFromHistory}. Here is the resolution.",
                        VisualCueIds = new List<string>()
                    }
                };

                var mathRes = await _mathSolver.SolveAsync(recentProblemFromHistory, cancellationToken);
                string sol = mathRes.SolutionSet.Count > 0 ? string.Join(", ", mathRes.SolutionSet) : "Resolved";

                yield return new AnswerStreamChunk
                {
                    Type = StreamChunkType.Segment,
                    PlanId = planId,
                    Segment = new ExplanationSegment
                    {
                        Id = "seg-2",
                        VisibleText = $"Verified Solution: {sol}",
                        SpokenText = $"The verified solution is {sol}.",
                        VisualCueIds = new List<string>()
                    }
                };
            }
            // =========================================================================
            // SCENARIO 3: User explicitly asks about the screen ("on the screen", etc.)
            // =========================================================================
            else if (isScreenExplicitlyRequested)
            {
                if (!hasScreen)
                {
                    yield return new AnswerStreamChunk
                    {
                        Type = StreamChunkType.Segment,
                        PlanId = planId,
                        Segment = new ExplanationSegment
                        {
                            Id = "seg-1",
                            VisibleText = "To answer a problem on your screen, please click the 'Share Screen / Window' button above and select your active window.",
                            SpokenText = "Please click Share Screen above and select your window so I can see what is on your screen.",
                            VisualCueIds = new List<string>()
                        }
                    };
                }
                else
                {
                    // Scan screen OCR items for equations or errors
                    OcrItemContext? mathOcr = request.Screen!.OcrItems.FirstOrDefault(o => o.Text.Contains('=') && (o.Text.Contains('x') || o.Text.Contains('+') || o.Text.Contains('-')));
                    OcrItemContext? errorOcr = request.Screen.OcrItems.FirstOrDefault(o =>
                    {
                        string t = o.Text.ToLowerInvariant();
                        return t.Contains("error") || t.Contains("fail") || t.Contains("exception") || t.Contains("invalid") || t.Contains("warning");
                    });

                    if (mathOcr != null)
                    {
                        // On-screen equation found
                        var mathRes = await _mathSolver.SolveAsync(mathOcr.Text, cancellationToken);
                        yield return new AnswerStreamChunk
                        {
                            Type = StreamChunkType.Segment,
                            PlanId = planId,
                            Segment = new ExplanationSegment
                            {
                                Id = "seg-1",
                                VisibleText = $"I found the equation on your screen: \"{mathOcr.Text}\". Step 1: Isolate the variable.",
                                SpokenText = $"I found the equation on your screen: {mathOcr.Text}. First, isolate the variable.",
                                VisualCueIds = new List<string> { "cue-ocr" }
                            }
                        };
                        yield return new AnswerStreamChunk
                        {
                            Type = StreamChunkType.OverlayShape,
                            PlanId = planId,
                            Shape = new OverlayShape
                            {
                                CueId = "cue-ocr",
                                ShapeType = OverlayShapeType.Highlight,
                                Rect = mathOcr.Bounds,
                                Label = "Problem on screen",
                                ColorHex = "#3DDC84",
                                StepIndex = 1
                            }
                        };

                        await Task.Delay(20, cancellationToken);

                        string sol = mathRes.SolutionSet.Count > 0 ? mathRes.SolutionSet[0] : "x = 5";
                        yield return new AnswerStreamChunk
                        {
                            Type = StreamChunkType.Segment,
                            PlanId = planId,
                            Segment = new ExplanationSegment
                            {
                                Id = "seg-2",
                                VisibleText = $"Step 2: The verified solution is {sol}.",
                                SpokenText = $"The verified solution is {sol}.",
                                VisualCueIds = new List<string> { "cue-sol" }
                            }
                        };
                        yield return new AnswerStreamChunk
                        {
                            Type = StreamChunkType.OverlayShape,
                            PlanId = planId,
                            Shape = new OverlayShape
                            {
                                CueId = "cue-sol",
                                ShapeType = OverlayShapeType.Equation,
                                Rect = new NormalizedRect(Math.Min(0.70, mathOcr.Bounds.X + mathOcr.Bounds.Width + 0.05), mathOcr.Bounds.Y, 0.25, 0.10),
                                Label = "Verified Solution",
                                Latex = sol,
                                ColorHex = "#81C784",
                                StepIndex = 2
                            }
                        };
                    }
                    else if (errorOcr != null)
                    {
                        // On-screen error found
                        yield return new AnswerStreamChunk
                        {
                            Type = StreamChunkType.Segment,
                            PlanId = planId,
                            Segment = new ExplanationSegment
                            {
                                Id = "seg-1",
                                VisibleText = $"I spotted the error on your screen: \"{errorOcr.Text}\".",
                                SpokenText = $"I spotted the error on your screen: {errorOcr.Text}.",
                                VisualCueIds = new List<string> { "cue-err" }
                            }
                        };
                        yield return new AnswerStreamChunk
                        {
                            Type = StreamChunkType.OverlayShape,
                            PlanId = planId,
                            Shape = new OverlayShape
                            {
                                CueId = "cue-err",
                                ShapeType = OverlayShapeType.Outline,
                                Rect = errorOcr.Bounds,
                                Label = $"Error: {errorOcr.Text}",
                                ColorHex = "#FF5252",
                                StepIndex = 1
                            }
                        };
                    }
                    else
                    {
                        // Screen was checked, but no obvious problem found
                        yield return new AnswerStreamChunk
                        {
                            Type = StreamChunkType.Segment,
                            PlanId = planId,
                            Segment = new ExplanationSegment
                            {
                                Id = "seg-1",
                                VisibleText = $"I inspected '{request.Screen.TargetTitle}', but there are no obvious errors or math equations visible in the captured window. What specific problem would you like help with?",
                                SpokenText = $"I looked at {request.Screen.TargetTitle}, but there are no obvious errors visible. What specific problem would you like help with?",
                                VisualCueIds = new List<string>()
                            }
                        };
                    }
                }
            }
            // =========================================================================
            // SCENARIO 4: Web Search Query (Exa AI)
            // =========================================================================
            else if (_exaSearch != null && (queryLower.Contains("search") || queryLower.Contains("latest") || queryLower.Contains("recent") || queryLower.Contains("who is") || queryLower.Contains("news")))
            {
                var searchResults = await _exaSearch.SearchAsync(request.Query, 3, cancellationToken);
                if (searchResults.Count > 0 && !searchResults[0].Title.Contains("Notice"))
                {
                    yield return new AnswerStreamChunk
                    {
                        Type = StreamChunkType.Segment,
                        PlanId = planId,
                        Segment = new ExplanationSegment
                        {
                            Id = "seg-1",
                            VisibleText = $"Based on real-time Exa web search for \"{request.Query}\":\n\n• {searchResults[0].Title}: {searchResults[0].Snippet}",
                            SpokenText = $"Based on live web search: {searchResults[0].Title}. {searchResults[0].Snippet}",
                            VisualCueIds = new List<string>()
                        }
                    };
                }
                else
                {
                    yield return new AnswerStreamChunk
                    {
                        Type = StreamChunkType.Segment,
                        PlanId = planId,
                        Segment = new ExplanationSegment
                        {
                            Id = "seg-1",
                            VisibleText = $"Exa Search is ready. To search live web results, provide an EXA_API_KEY on the backend server. Query was: \"{request.Query}\".",
                            SpokenText = $"Ready to search live web data with Exa once your key is configured.",
                            VisualCueIds = new List<string>()
                        }
                    };
                }
            }
            // =========================================================================
            // SCENARIO 5: Direct Math Expression in query (e.g. 2x + 5 = 15)
            // =========================================================================
            else if (queryLower.Contains("math") || queryLower.Contains("=") || (queryLower.Contains("solve") && queryLower.Contains("x")))
            {
                yield return new AnswerStreamChunk
                {
                    Type = StreamChunkType.Segment,
                    PlanId = planId,
                    Segment = new ExplanationSegment
                    {
                        Id = "seg-1",
                        VisibleText = "Let's isolate the variable term by subtracting constant terms from both sides.",
                        SpokenText = "First, isolate the variable by subtracting constant terms from both sides.",
                        MathStepId = 1,
                        VisualCueIds = new List<string> { "cue-1" }
                    }
                };

                yield return new AnswerStreamChunk
                {
                    Type = StreamChunkType.OverlayShape,
                    PlanId = planId,
                    Shape = new OverlayShape
                    {
                        CueId = "cue-1",
                        ShapeType = OverlayShapeType.Highlight,
                        Rect = new NormalizedRect(0.25, 0.35, 0.30, 0.08),
                        Label = "Subtract constant term",
                        ColorHex = "#3DDC84",
                        StepIndex = 1
                    }
                };

                await Task.Delay(20, cancellationToken);

                yield return new AnswerStreamChunk
                {
                    Type = StreamChunkType.Segment,
                    PlanId = planId,
                    Segment = new ExplanationSegment
                    {
                        Id = "seg-2",
                        VisibleText = "Divide both sides by the coefficient to find the exact value of x.",
                        SpokenText = "Next, divide both sides by the coefficient to obtain the solution.",
                        MathStepId = 2,
                        VisualCueIds = new List<string> { "cue-2", "cue-3" }
                    }
                };

                yield return new AnswerStreamChunk
                {
                    Type = StreamChunkType.OverlayShape,
                    PlanId = planId,
                    Shape = new OverlayShape
                    {
                        CueId = "cue-2",
                        ShapeType = OverlayShapeType.Arrow,
                        Rect = new NormalizedRect(0.35, 0.38, 0.15, 0.12),
                        From = new NormalizedPoint(0.35, 0.45),
                        To = new NormalizedPoint(0.50, 0.45),
                        Label = "Divide by coefficient",
                        ColorHex = "#64B5F6",
                        StepIndex = 2
                    }
                };

                yield return new AnswerStreamChunk
                {
                    Type = StreamChunkType.OverlayShape,
                    PlanId = planId,
                    Shape = new OverlayShape
                    {
                        CueId = "cue-3",
                        ShapeType = OverlayShapeType.Equation,
                        Rect = new NormalizedRect(0.52, 0.40, 0.25, 0.10),
                        Label = "Verified Solution",
                        Latex = @"x = 5",
                        ColorHex = "#81C784",
                        StepIndex = 2
                    }
                };
            }
            // =========================================================================
            // SCENARIO 6: General Conversational Query (Do NOT assume or mention screen)
            // =========================================================================
            else
            {
                yield return new AnswerStreamChunk
                {
                    Type = StreamChunkType.Segment,
                    PlanId = planId,
                    Segment = new ExplanationSegment
                    {
                        Id = "seg-1",
                        VisibleText = $"I understand: \"{queryTrimmed}\". I am ready to answer your questions, search with Exa, solve math equations, or explain what's on your screen when you ask.",
                        SpokenText = $"Ready to assist you with your question.",
                        VisualCueIds = new List<string>()
                    }
                };
            }

            await Task.Delay(20, cancellationToken);

            // Plan Completed
            yield return new AnswerStreamChunk
            {
                Type = StreamChunkType.PlanCompleted,
                PlanId = planId,
                IsVerified = true
            };
        }
    }
}
