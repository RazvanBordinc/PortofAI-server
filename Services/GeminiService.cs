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
            _apiKey = configuration["GeminiApi:ApiKey"] ??
                      Environment.GetEnvironmentVariable("GOOGLE_API_KEY")!;

            if (string.IsNullOrEmpty(_apiKey))
            {
                _logger.LogError("Gemini API key not configured. Please set GeminiApi:ApiKey in configuration or GOOGLE_API_KEY environment variable.");
                throw new InvalidOperationException("Gemini API key not configured");
            }

            _logger.LogInformation($"API key configured with length: {_apiKey.Length}");

            // Get model name
            _modelName = configuration["GeminiApi:ModelName"] ?? "gemini-2.0-flash";
            _logger.LogInformation($"Using model: {_modelName}");

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

            _logger.LogInformation($"GeminiService initialized with model: {_modelName}");
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

        // Simplified streaming implementation
        public async Task<string> StreamMessageAsync(
            string message,
            string sessionId,
            string style,
            Func<string, Task> onChunkReceived,
            CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogInformation($"Streaming message with style: {style}");

                // Get conversation history
                string conversationHistory = await _conversationService.GetConversationHistoryAsync(sessionId);

                // Build prompt
                string promptText = BuildPrompt(message, conversationHistory, style);

                // Instead of streaming, use the non-streaming method and simulate streaming
                // This is a temporary fallback until the streaming issues are fixed
                string fullResponse = await CallGeminiApiAsync(promptText);

                _logger.LogInformation($"Generated full response length: {fullResponse?.Length ?? 0}");

                if (string.IsNullOrEmpty(fullResponse))
                {
                    // If we got no response, create a fallback
                    fullResponse = "I'm sorry, I couldn't generate a response at this time. Please try again later.";
                    await onChunkReceived(fullResponse);
                    return fullResponse;
                }

                // Simulate streaming by chunking the response
                int chunkSize = 25; // Characters per chunk

                for (int i = 0; i < fullResponse.Length; i += chunkSize)
                {
                    // Check for cancellation
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    // Get chunk (up to chunkSize or end of text)
                    int remainingLength = Math.Min(chunkSize, fullResponse.Length - i);
                    string chunk = fullResponse.Substring(i, remainingLength);

                    // Send chunk to client
                    await onChunkReceived(chunk);

                    // Small delay to simulate typing
                    await Task.Delay(50, cancellationToken);
                }

                // Check for contact query in original message
                if (IsContactQuery(message) && !fullResponse.Contains("[format:contact]"))
                {
                    fullResponse = EnhanceContactResponse(fullResponse);
                }

                return fullResponse;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in StreamMessageAsync");
                await onChunkReceived("I'm sorry, I encountered a technical issue and couldn't process your request.");
                return "Error processing request";
            }
        }

        private bool IsContactQuery(string message)
        {
            string lowerMessage = message.ToLower();
            return lowerMessage.Contains("contact") ||
                   lowerMessage.Contains("email") ||
                   lowerMessage.Contains("reach") ||
                   lowerMessage.Contains("message you") ||
                   lowerMessage.Contains("get in touch") ||
                   lowerMessage.Contains("connect with you");
        }

        private string EnhanceContactResponse(string response)
        {
            try
            {
                // Create a contact form data object
                var contactData = new
                {
                    title = "Contact Form",
                    recipientName = "Razvan Bordinc",
                    recipientPosition = "Software Engineer",
                    emailSubject = "Portfolio Contact",
                    socialLinks = new[]
                    {
                new
                {
                    platform = "LinkedIn",
                    url = "https://linkedin.com/in/valentin-r%C4%83zvan-bord%C3%AEnc-30686a298/",
                    icon = "linkedin"
                },
                new
                {
                    platform = "GitHub",
                    url = "https://github.com/RazvanBordinc",
                    icon = "github"
                },
                new
                {
                    platform = "Email",
                    url = "mailto:razvan.bordinc@yahoo.com",
                    icon = "mail"
                }
            }
                };

                // Serialize to JSON without indentation to avoid formatting issues
                string jsonData = JsonSerializer.Serialize(contactData, _jsonOptions);

                // Format the contact response
                return $"{response}\n\nYou can contact me using the form below or directly at razvan.bordinc@yahoo.com:\n\n[format:contact][data:{jsonData}][/format]";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating contact form JSON");
                return response;
            }
        }

        private string BuildPrompt(string message, string conversationHistory, string style)
        {
            var promptBuilder = new StringBuilder();

            // Add system instructions with CORRECT contact information
            promptBuilder.AppendLine("You are an AI chatbot representing Razvan Bordinc, a software engineer. Use the information in the portfolio data to answer questions accurately about Razvan's skills, projects, and experience. If you don't know something, be honest and don't make up information.");
            promptBuilder.AppendLine("\nImportant contact information:");
            promptBuilder.AppendLine("- Email: razvan.bordinc@yahoo.com");
            promptBuilder.AppendLine("- GitHub: https://github.com/RazvanBordinc");
            promptBuilder.AppendLine("- LinkedIn: https://linkedin.com/in/valentin-r%C4%83zvan-bord%C3%AEnc-30686a298/");

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

            // Add special instructions if needed
            if (IsContactQuery(message))
            {
                promptBuilder.AppendLine("\nThis is a contact-related query. Please provide my contact information. Always use razvan.bordinc@yahoo.com as the email address.");
            }

            return promptBuilder.ToString();
        }


        private string GetStyleInstruction(string style)
        {
            return style.ToUpper() switch
            {
                "FORMAL" => "Respond in a formal, professional tone. Use proper grammar and avoid contractions or colloquialisms. Structure your responses clearly with proper paragraphs.",
                "EXPLANATORY" => "Respond in a teaching style that explains concepts thoroughly. Use examples where appropriate and break down complex ideas into simpler components. Number your points when listing multiple items.",
                "MINIMALIST" => "Respond with brevity. Keep answers concise and to the point. Avoid unnecessary elaboration and focus on delivering essential information only.",
                "HR" => "Respond in a warm, professional tone suitable for HR or recruitment conversations. Emphasize professional achievements, soft skills, and culture fit aspects.",
                _ => "Respond in a balanced, conversational tone. Be helpful, clear, and friendly."
            };
        }

        private async Task<string> CallGeminiApiAsync(string promptText)
        {
            try
            {
                // Construct the API endpoint URL with the API key
                string apiUrl = $"v1beta/models/{_modelName}:generateContent?key={_apiKey}";
                _logger.LogDebug($"Using API URL: {apiUrl.Replace(_apiKey, "API_KEY_REDACTED")}");

                // Prepare the request payload
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

                _logger.LogInformation("Sending request to Gemini API");

                // Send the request
                var response = await _httpClient.PostAsync(apiUrl, content);

                // Log response code
                _logger.LogDebug($"Gemini API response status code: {(int)response.StatusCode} {response.StatusCode}");

                // If the response is not successful, handle different error cases
                if (!response.IsSuccessStatusCode)
                {
                    string errorMessage = await response.Content.ReadAsStringAsync();
                    _logger.LogError($"Gemini API Error: Status {response.StatusCode}, Message: {errorMessage}");

                    // Provide a more helpful error message based on status code
                    return (int)response.StatusCode switch
                    {
                        400 => "I'm sorry, there was an issue with the request. Please try again later.",
                        401 => "I'm currently unable to access my knowledge due to authentication issues. Please check your API key configuration.",
                        403 => "I don't have permission to access that information right now.",
                        404 => "I'm sorry, I couldn't find the information you're looking for.",
                        429 => "I'm currently experiencing high demand. Please try again in a little while.",
                        _ => "I'm sorry, I'm having trouble processing your request right now. Please try again later."
                    };
                }

                // Parse the response
                string responseJson = await response.Content.ReadAsStringAsync();
                _logger.LogDebug($"Received response from Gemini API, length: {responseJson.Length}");

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
                                        _logger.LogInformation($"Successfully extracted response text (length: {text.Length})");
                                        return text;
                                    }
                                }
                            }
                        }
                    }

                    // If we got here, we couldn't extract the text
                    _logger.LogWarning("Could not extract text from Gemini API response. JSON structure: " + responseJson);

                    // Return a fallback response
                    return "I'm sorry, I couldn't generate a proper response. Please try again.";
                }
                catch (JsonException ex)
                {
                    _logger.LogError(ex, "Error parsing Gemini API response JSON");
                    return "I'm sorry, there was an error processing the response from my knowledge source.";
                }
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "HTTP error calling Gemini API");
                return "I'm having trouble connecting to my knowledge source right now. Please try again later.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error calling Gemini API");
                return "I'm sorry, an unexpected error occurred while processing your request.";
            }
        }

        private ProcessedResponse ProcessResponse(string response)
        {
            try
            {
                // Default response format
                var processedResponse = new ProcessedResponse
                {
                    Text = response,
                    Format = "text"
                };

                // Check for format tag
                var formatRegex = new Regex(@"\[format:(\w+)\]");
                var formatMatch = formatRegex.Match(response);

                if (formatMatch.Success)
                {
                    processedResponse.Format = formatMatch.Groups[1].Value.ToLower();

                    // Extract data if present
                    var dataRegex = new Regex(@"\[data:(.*?)\]", RegexOptions.Singleline);
                    var dataMatch = dataRegex.Match(response);

                    if (dataMatch.Success)
                    {
                        try
                        {
                            string jsonData = dataMatch.Groups[1].Value;
                            _logger.LogDebug($"Extracted JSON data: {jsonData}");

                            // Try to deserialize the JSON string
                            try
                            {
                                var data = JsonSerializer.Deserialize<object>(jsonData, _jsonOptions);
                                processedResponse.FormatData = data;
                            }
                            catch (JsonException jsonEx)
                            {
                                _logger.LogWarning(jsonEx, "JSON parsing failed, attempting to clean JSON string");

                                // Attempt to clean the JSON string
                                jsonData = CleanJsonString(jsonData);

                                // Try parsing again with cleaned JSON
                                var data = JsonSerializer.Deserialize<object>(jsonData, _jsonOptions);
                                processedResponse.FormatData = data;

                                _logger.LogInformation("Successfully parsed JSON after cleaning");
                            }

                            // Remove the data from the text
                            response = dataRegex.Replace(response, "");
                        }
                        catch (JsonException ex)
                        {
                            _logger.LogWarning(ex, "Error parsing JSON data from response even after cleaning");
                        }
                    }

                    // Remove format tags
                    response = formatRegex.Replace(response, "");
                    response = Regex.Replace(response, @"\[\/format\]", "");
                    processedResponse.Text = response.Trim();
                }

                return processedResponse;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing Gemini API response");
                return new ProcessedResponse { Text = response, Format = "text" };
            }
        }

        private string CleanJsonString(string jsonStr)
        {
            if (string.IsNullOrEmpty(jsonStr))
                return jsonStr;

            // Remove any potential trailing characters that might break JSON
            jsonStr = Regex.Replace(jsonStr, @"\s*\]\s*\}\s*$", "]}");

            // Replace unescaped newlines inside strings
            jsonStr = Regex.Replace(jsonStr, @"(?<!\\)(""|')([^""]*?)(?<!\\)\n([^""]*?)(?<!\\)(""|')", "$1$2 $3$4");

            // Replace JavaScript-style property names (without quotes) with JSON-style (with quotes)
            jsonStr = Regex.Replace(jsonStr, @"([{,])\s*([a-zA-Z0-9_$]+)\s*:", "$1\"$2\":");

            // Replace single quotes with double quotes (handling escaped quotes)
            jsonStr = jsonStr
                .Replace("\\'", "\\TEMP_QUOTE")  // Temporarily replace escaped single quotes
                .Replace("'", "\"")              // Replace all single quotes with double quotes
                .Replace("\\TEMP_QUOTE", "\\'"); // Restore escaped single quotes

            // Remove trailing commas in objects and arrays
            jsonStr = Regex.Replace(jsonStr, @",\s*}", "}");
            jsonStr = Regex.Replace(jsonStr, @",\s*\]", "]");

            // Fix unescaped control characters
            jsonStr = Regex.Replace(jsonStr, @"[\x00-\x1F]", " ");

            return jsonStr;
        }

        // Helper method for debugging
        public string GetModelName()
        {
            return _modelName;
        }

        // Helper method to debug API key
        public string GetApiKeyPreview()
        {
            if (string.IsNullOrEmpty(_apiKey))
                return "Not set";

            if (_apiKey.Length <= 5)
                return "Too short";

            return $"{_apiKey.Substring(0, 5)}... (length: {_apiKey.Length})";
        }
    }
}