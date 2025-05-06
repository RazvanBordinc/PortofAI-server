using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Portfolio_server.Models;

namespace Portfolio_server.Services
{
    public class GeminiService : IGeminiService
    {
        private readonly ILogger<GeminiService> _logger;
        private readonly HttpClient _httpClient;
        private readonly IConversationService _conversationService;
        private readonly string _apiKey;
        private readonly string _modelName;
        private readonly JsonSerializerOptions _jsonOptions;

        public GeminiService(
        ILogger<GeminiService> logger,
        HttpClient httpClient,
        IConversationService conversationService,
        IConfiguration configuration)
        {
            _logger = logger;
            _httpClient = httpClient;
            _conversationService = conversationService;

            // Get API key
            var envKey1 = Environment.GetEnvironmentVariable("GOOGLE_API_KEY");
            var envKey2 = configuration["GOOGLE_API_KEY"];
            var configKey = configuration["GeminiApi:ApiKey"];
            _apiKey = configKey ?? envKey1 ?? envKey2 ?? "";
            _logger.LogWarning($"Config key found: {!string.IsNullOrEmpty(configKey)}, length: {configKey?.Length ?? 0}");
            _logger.LogWarning($"Env key1 found: {!string.IsNullOrEmpty(envKey1)}, length: {envKey1?.Length ?? 0}");
            _logger.LogWarning($"Env key2 found: {!string.IsNullOrEmpty(envKey2)}, length: {envKey2?.Length ?? 0}");

            if (string.IsNullOrEmpty(_apiKey))
            {
                _logger.LogError("Gemini API key not configured. Please set GeminiApi:ApiKey in configuration or GOOGLE_API_KEY environment variable.");
                throw new InvalidOperationException("Gemini API key not configured");
            }

            // Reduced unnecessary logging
            _logger.LogDebug($"API key configured with length: {_apiKey.Length}");

            // Get model name
            _modelName = configuration["GeminiApi:ModelName"] ?? "gemini-2.0-flash";
            _logger.LogDebug($"Using model: {_modelName}");

            // Configure JSON options
            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = false
            };

            // Set base address
            if (_httpClient.BaseAddress == null)
            {
                _httpClient.BaseAddress = new Uri("https://generativelanguage.googleapis.com/");
            }

            _logger.LogDebug($"GeminiService initialized with model: {_modelName}");
        }

        public async Task<ProcessedResponse> ProcessMessageAsync(string message, string sessionId, string style = "NORMAL")
        {
            try
            {
                _logger.LogInformation($"Processing message with style: {style}");

                // Get history
                string conversationHistory = await _conversationService.GetConversationHistoryAsync(sessionId);

                // Build prompt
                string promptText = BuildPrompt(message, conversationHistory, style);

                // Call API
                string response = await CallGeminiApiAsync(promptText);

                // Process response
                return ProcessResponse(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing message with GeminiService");
                return new ProcessedResponse
                {
                    Text = "I'm sorry, I encountered a technical issue and couldn't process your request. Please try again later.",
                    Format = "text"
                };
            }
        }

        public async Task<string> StreamMessageAsync(
         string message,
        string sessionId,
        string style,
        Func<string, Task> onChunkReceived,
        CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogDebug($"Streaming message with style: {style}");

                // Get conversation history
                string conversationHistory = await _conversationService.GetConversationHistoryAsync(sessionId);

                // Build prompt
                string promptText = BuildPrompt(message, conversationHistory, style);

                try
                {
                    // Try to call the Gemini API
                    string fullResponse = await CallGeminiApiAsync(promptText);

                    _logger.LogDebug($"Generated full response length: {fullResponse?.Length ?? 0}");

                    if (string.IsNullOrEmpty(fullResponse))
                    {
                        // If we got no response, create a fallback
                        fullResponse = "I'm sorry, I couldn't generate a response at this time. Please try again later.";
                        await onChunkReceived(fullResponse);
                        return fullResponse;
                    }

                    // Check if response contains error message about API overload
                    bool isErrorResponse = fullResponse.Contains("The Gemini API is currently overloaded") ||
                                          fullResponse.Contains("technical issue connecting");

                    if (isErrorResponse)
                    {
                        // For error responses, don't simulate streaming - send it all at once
                        await onChunkReceived(fullResponse);
                        return fullResponse;
                    }

                    // Clean full response to fix any formatting issues before streaming
                    fullResponse = CleanMarkdownLinks(fullResponse);

                    // For successful responses, simulate streaming by chunking
                    int chunkSize = 25; // Characters per chunk

                    for (int i = 0; i < fullResponse.Length; i += chunkSize)
                    {
                        // Check for cancellation
                        if (cancellationToken.IsCancellationRequested)
                            break;

                        // Get chunk (up to chunkSize or end of text)
                        int remainingLength = Math.Min(chunkSize, fullResponse.Length - i);
                        string chunk = fullResponse.Substring(i, remainingLength);

                        // Clean the chunk before sending
                        chunk = CleanMarkdownLinks(chunk);

                        // Send chunk to client
                        await onChunkReceived(chunk);

                        // Small delay to simulate typing
                        await Task.Delay(50, cancellationToken);
                    }

                   

                    // One final cleaning pass to ensure the complete response is well-formatted
                    return CleanMarkdownLinks(fullResponse);
                }
                catch (HttpRequestException httpEx)
                {
                    _logger.LogError(httpEx, $"HTTP error calling Gemini API: {httpEx.Message}");

                    // Create a nicely formatted response with examples of styling for testing
                    string errorResponse = "I apologize, but I'm currently experiencing connectivity issues with my AI service. " +
                        "This is likely a temporary problem.\n\n" +
                        "While I can't answer your specific question right now, you can:\n\n" +
                        "1. Try again in a few minutes\n" +
                        "2. Contact Razvan directly at **razvan.bordinc@yahoo.com**\n" +
                        "3. Visit the GitHub profile at https://github.com/RazvanBordinc\n\n" +
                        "Error details: " + httpEx.Message;

                    await onChunkReceived(errorResponse);
                    return errorResponse;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in StreamMessageAsync");

                // Create a fallback response with properly formatted text to test styling
                string fallbackResponse = "I'm sorry, I encountered a technical issue and couldn't process your request.\n\n" +
                    "In the meantime, you can contact me directly at razvan.bordinc@yahoo.com or check out my GitHub at https://github.com/RazvanBordinc.\n\n" +
                    "Try again later when the service is available.";

                // Send the fallback response as a chunk
                await onChunkReceived(fallbackResponse);

                return fallbackResponse;
            }
        }

        // Enhanced BuildPrompt method with better prompt engineering
        private string BuildPrompt(string message, string conversationHistory, string style)
        {
            var promptBuilder = new StringBuilder();

            // Basic instructions
            promptBuilder.AppendLine("You are an AI chatbot representing Razvan Bordinc, a software engineer. Use the information in the PORTFOLIO INFORMATION section to answer questions accurately about Razvan's skills, projects, and data about him, including experience.");

            promptBuilder.AppendLine("CRITICAL INSTRUCTION:");
            promptBuilder.AppendLine("DETECT THE LANGUAGE IT WAS WRITTEN TO YOU AND WRITE IN THE SAME LANGUAGE");
            promptBuilder.AppendLine("RESPOND MANDATORY BASED ON THE INFORMATIONS YOU GOT ABOUT ME WITH TRUE FACTS ONLY!");

            promptBuilder.AppendLine("FORMAT INSTRUCTIONS:");
            promptBuilder.AppendLine("- If the user asks for ways to contact me or reach out to me, include the contact form using: [format:contact][data:...][/format]");
            promptBuilder.AppendLine("- For all other questions, use regular text format");
            promptBuilder.AppendLine("- Do not include contact form unless specifically requested");

 
            promptBuilder.AppendLine("CONTACT FORM INSTRUCTIONS:");
            promptBuilder.AppendLine("- When the user asks about contact information, ALWAYS include the contact form");
            promptBuilder.AppendLine("- Include contact form for questions like: 'how to contact', 'get in touch', 'reach out', etc.");

            promptBuilder.AppendLine("RESPOND STRICTLY TO WHAT THE USER ASKS...BUT IN A HUMAN AND CREATIVE WAY, ALSO STRUCTURE YOUR RERSPONSE");

            // Add style instructions based on the specified style
            promptBuilder.AppendLine("\n" + GetStyleInstruction(style));

            // Add conversation history for context if available
            if (!string.IsNullOrWhiteSpace(conversationHistory))
            {
                promptBuilder.AppendLine("\nConversation history:");
                promptBuilder.AppendLine(conversationHistory);
            }

            // Add the current message
            promptBuilder.AppendLine("\nCurrent message:");
            promptBuilder.AppendLine(message);

            return promptBuilder.ToString();
        }


        private string GetStyleInstruction(string style)
        {
            return style.ToUpper() switch
            {
                "FORMAL" => "Respond in a formal, professional tone. Use proper grammar and avoid contractions or colloquialisms. Structure your responses clearly with proper paragraphs, while feeling human.",
                "EXPLANATORY" => "Respond in a teaching style that explains concepts thoroughly. Use examples where appropriate and break down complex ideas into simpler components. Number your points when listing multiple items.",
                "MINIMALIST" => "Respond with brevity. Keep answers concise and to the point. Avoid unnecessary elaboration and focus on delivering essential information only.",
                "HR" => "Respond in a warm, professional tone suitable for HR or recruitment conversations. Emphasize professional achievements, soft skills, and culture fit aspects.",
                _ => "Respond in a balanced, conversational tone. Be helpful, clear, and friendly."
            };
        }

        private async Task<string> CallGeminiApiAsync(string promptText)
        {
            int maxRetries = 5;  
            int currentRetry = 0;
            int baseDelayMs = 1000; // Start with 1 second delay
            Exception lastException = null;

            while (true)
            {
                try
                {
                    // Construct the API endpoint URL with the API key
                    string apiUrl = $"v1beta/models/{_modelName}:generateContent?key={_apiKey}";

 
                    var requestData = new
                    {
                        contents = new[]
                        {
                    new
                    {
                        role = "user",
                        parts = new[]
                        {
                            new { text = promptText }
                        }
                    }
                },
                        generationConfig = new
                        {
                            temperature = 0.7,
                            topK = 40,
                            topP = 0.95,
                            maxOutputTokens = 8192,
                            stopSequences = Array.Empty<string>()
                        },
                        safetySettings = new[]
                        {
                    new { category = "HARM_CATEGORY_HARASSMENT", threshold = "BLOCK_MEDIUM_AND_ABOVE" },
                    new { category = "HARM_CATEGORY_HATE_SPEECH", threshold = "BLOCK_MEDIUM_AND_ABOVE" },
                    new { category = "HARM_CATEGORY_SEXUALLY_EXPLICIT", threshold = "BLOCK_MEDIUM_AND_ABOVE" },
                    new { category = "HARM_CATEGORY_DANGEROUS_CONTENT", threshold = "BLOCK_MEDIUM_AND_ABOVE" }
                }
                    };

                    // Serialize to JSON
                    string jsonContent = JsonSerializer.Serialize(requestData, _jsonOptions);
                    var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                    // Send the request with timeout - increase timeout for retry attempts
                    int timeoutSeconds = 15 + (currentRetry * 5); // Add 5 seconds per retry
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));

                    // Log the attempt
                    _logger.LogInformation($"Calling Gemini API (attempt {currentRetry + 1}/{maxRetries}, timeout: {timeoutSeconds}s)");

                    var response = await _httpClient.PostAsync(apiUrl, content, cts.Token);

                    // If the response is not successful, handle different error cases
                    if (!response.IsSuccessStatusCode)
                    {
                        string errorMessage = await response.Content.ReadAsStringAsync();
                        _logger.LogError($"Gemini API Error: Status {response.StatusCode}, Message: {errorMessage}");

                        // For 503 (service unavailable) or 429 (rate limit), retry with backoff
                        if ((int)response.StatusCode == 503 || (int)response.StatusCode == 429)
                        {
                            if (currentRetry < maxRetries)
                            {
                                // Calculate delay with exponential backoff and jitter
                                int delayMs = baseDelayMs * (int)Math.Pow(2, currentRetry);
                                // Add some randomness to avoid thundering herd problem
                                delayMs += new Random().Next(100, 500);

                                _logger.LogWarning($"Gemini API returned {response.StatusCode}, retrying in {delayMs}ms (attempt {currentRetry + 1}/{maxRetries})");

                                await Task.Delay(delayMs);
                                currentRetry++;
                                continue; // Try again
                            }

                            // If we've reached max retries, return a friendly error message
                            return $"I'm sorry, but the AI service is currently experiencing high traffic. Please try again in a few moments.\n\nIn the meantime, you can contact me directly at razvan.bordinc@yahoo.com or check out my GitHub at https://github.com/RazvanBordinc.\n\nError details: {response.StatusCode} after {maxRetries} attempts.";
                        }

                        // Handle other error cases with appropriate messages
                        return (int)response.StatusCode switch
                        {
                            400 => "I apologize, but there was an error with the request format. Please try again with a simpler message.",
                            401 or 403 => "I'm experiencing authentication issues with my AI service. This is likely a temporary problem with my API configuration.",
                            404 => "The AI model endpoint couldn't be found. This is likely a temporary configuration issue.",
                            _ => $"I'm having trouble connecting to my knowledge source right now. Please try again later. (Error: {response.StatusCode})"
                        };
                    }

                    // Parse the response
                    string responseJson = await response.Content.ReadAsStringAsync();
                    _logger.LogDebug($"Received response from Gemini API (length: {responseJson.Length})");

                    try
                    {
                        // Try to parse the JSON response
                        var responseObj = JsonSerializer.Deserialize<JsonElement>(responseJson);

                        // Check if candidates array exists and has at least one element
                        if (responseObj.TryGetProperty("candidates", out var candidates) &&
                            candidates.ValueKind == JsonValueKind.Array &&
                            candidates.GetArrayLength() > 0)
                        {
                            // Get the first candidate
                            var candidate = candidates[0];

                            // Check if it has content
                            if (candidate.TryGetProperty("content", out var contentT))
                            {
                                // Check if content has parts
                                if (contentT.TryGetProperty("parts", out var parts) &&
                                    parts.ValueKind == JsonValueKind.Array &&
                                    parts.GetArrayLength() > 0)
                                {
                                    // Get first part
                                    var part = parts[0];

                                    // Get text
                                    if (part.TryGetProperty("text", out var textElement))
                                    {
                                        string text = textElement.GetString();
                                        if (!string.IsNullOrEmpty(text))
                                        {
                                            // Clean the text before returning
                                            return CleanMarkdownLinks(text);
                                        }
                                    }
                                }
                            }
                        }

                        // If we got here, we couldn't extract the text
                        _logger.LogWarning("Could not extract text from Gemini API response");
                        return "I'm sorry, I couldn't generate a proper response. Please try again with a different question.";
                    }
                    catch (JsonException ex)
                    {
                        _logger.LogError(ex, "Error parsing Gemini API response JSON");
                        return "I'm sorry, there was an error processing the response from my knowledge source.";
                    }
                }
                catch (TaskCanceledException)
                {
                    _logger.LogWarning($"Gemini API request timed out (attempt {currentRetry + 1}/{maxRetries})");

                    if (currentRetry < maxRetries)
                    {
                        // Calculate delay with exponential backoff
                        int delayMs = baseDelayMs * (int)Math.Pow(2, currentRetry);
                        _logger.LogWarning($"Request timed out, retrying in {delayMs}ms (attempt {currentRetry + 1}/{maxRetries})");

                        await Task.Delay(delayMs);
                        currentRetry++;
                        continue; // Try again
                    }

                    return "I'm sorry, but the request is taking longer than expected. Please try again with a shorter message, or try again later.";
                }
                catch (HttpRequestException ex)
                {
                    lastException = ex;
                    _logger.LogError(ex, $"HTTP error calling Gemini API (attempt {currentRetry + 1}/{maxRetries})");

                    if (currentRetry < maxRetries)
                    {
                        // Calculate delay with exponential backoff
                        int delayMs = baseDelayMs * (int)Math.Pow(2, currentRetry);
                        _logger.LogWarning($"HTTP error, retrying in {delayMs}ms (attempt {currentRetry + 1}/{maxRetries})");

                        await Task.Delay(delayMs);
                        currentRetry++;
                        continue; // Try again
                    }

                    return $"I'm having trouble connecting to my knowledge source right now. Please try again later. Error details: {ex.Message}";
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    _logger.LogError(ex, $"Unexpected error calling Gemini API (attempt {currentRetry + 1}/{maxRetries})");

                    if (currentRetry < maxRetries)
                    {
                        // Calculate delay with exponential backoff
                        int delayMs = baseDelayMs * (int)Math.Pow(2, currentRetry);
                        _logger.LogWarning($"Unexpected error, retrying in {delayMs}ms (attempt {currentRetry + 1}/{maxRetries})");

                        await Task.Delay(delayMs);
                        currentRetry++;
                        continue; // Try again
                    }

                    return "I'm sorry, an unexpected error occurred while processing your request.";
                }
            }
        }

        private ProcessedResponse ProcessResponse(string response)
        {
            try
            {
                // Clean any link formatting issues first
                response = CleanMarkdownLinks(response);

                // Default response format
                var processedResponse = new ProcessedResponse
                {
                    Text = response,
                    Format = "text"
                };

                // Rest of your existing processing code...

                return processedResponse;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing Gemini API response");
                return new ProcessedResponse { Text = response, Format = "text" };
            }
        }
        private string CleanMarkdownLinks(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            // Fix malformed markdown links: [text](url))
            text = Regex.Replace(text, @"\[([^\]]+)\]\(([^)]+)\)\)+", "[$1]($2)");

            // Fix links with extra brackets: [text](url))}]
            text = Regex.Replace(text, @"\[([^\]]+)\]\(([^)]+)\)[\)\}\]]+", "[$1]($2)");

            // Clean up any trailing JSON-like syntax artifacts
            text = Regex.Replace(text, @"[\}\]:\}\]]+$", "");

            // Fix any escaped brackets in URLs
            text = Regex.Replace(text, @"\\\[", "[");
            text = Regex.Replace(text, @"\\\]", "]");

            return text;
        }
    
    }
}