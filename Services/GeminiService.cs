using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Net.Http;
using System.Reflection.Metadata;
using System.Runtime.Intrinsics.X86;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore.Metadata.Internal;
using System.Xml;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.SqlServer.Server;
using Portfolio_server.Controllers;
using Portfolio_server.Models;
using StackExchange.Redis;
using static System.Net.Mime.MediaTypeNames;
using Microsoft.EntityFrameworkCore.Metadata;
using System.Diagnostics.Contracts;
using System.Xml.Linq;

namespace Portfolio_server.Services
{


    public class GeminiService : IGeminiService
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<GeminiService> _logger;
        private readonly IConversationService _conversationService;
        private readonly string _apiKey;
        private readonly string _modelName;

        public GeminiService(
            HttpClient httpClient,
            IConfiguration configuration,
            ILogger<GeminiService> logger,
            IConversationService conversationService)
        {
            _httpClient = httpClient;
            _logger = logger;
            _conversationService = conversationService;

            // Get API key and model name from configuration
            _apiKey = configuration["GeminiApi:ApiKey"];
            _modelName = configuration["GeminiApi:ModelName"] ?? "gemini-2.0-flash";

            // Configure base address
            _httpClient.BaseAddress = new Uri("https://generativelanguage.googleapis.com/");
        }

        public async Task<ProcessedResponse> ProcessMessageAsync(string message, string sessionId, string style = "NORMAL")
        {
            try
            {
                if (string.IsNullOrEmpty(_apiKey))
                {
                    _logger.LogError("Gemini API key is not configured");
                    return new ProcessedResponse
                    {
                        Text = "I'm sorry, the AI service is not properly configured. The API key for Google Gemini is missing.",
                        Format = "text"
                    };
                }

                // Get conversation history
                string conversationHistory = await _conversationService.GetConversationHistoryAsync(sessionId);

                // Generate the prompt with style instructions
                string promptText = GeneratePrompt(message, conversationHistory, style);

                // Call Gemini API
                var response = await CallGeminiApiAsync(promptText);

                // Process response to extract format information
                var processedResponse = ProcessGeminiResponse(response);

                // Save the conversation
                await _conversationService.SaveConversationAsync(sessionId, message, processedResponse.Text);

                return processedResponse;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing message with Gemini API");

                return new ProcessedResponse
                {
                    Text = "I'm sorry, I encountered an error processing your request. Please try again later.",
                    Format = "text"
                };
            }
        }

        private string GeneratePrompt(string message, string conversationHistory, string style)
        {
            var styleInstructions = GetStyleInstructions(style);

 
            var promptText = $@"You are a helpful, intelligent AI assistant for a portfolio website.

            {styleInstructions}
            
            You can enhance your responses by using special format tags for rich content display:
            - [format:text]   – Plain text format (default if no format is specified)
            - [format:contact]– Use when sharing contact information or a contact form
            
            For [format:contact], include a JSON data object with contact information. FOLLOW THIS EXACT FORMAT:
               [data:{{""title"": ""Contact Information"", ""recipientName"": ""Your Name"", ""recipientPosition"": ""Your Position"", ""emailSubject"": ""Inquiry from Portfolio"", ""socialLinks"": [{{""platform"": ""LinkedIn"", ""url"": ""https://linkedin.com/in/example"", ""icon"": ""linkedin""}}]}}]
            
            CRITICAL JSON FORMATTING RULES:
            - Always use double quotes ("" "") for property names and string values, never single quotes (' ')
            - Don't include trailing commas (e.g., [1, 2, 3,] or {{""a"": 1, ""b"": 2,}})
            - Make sure all JSON property names have quotes around them (e.g., {{""name"": ""value""}})
            - Ensure all brackets and braces are properly balanced and closed
            - Test your JSON format before including it in your response
            
            Current conversation:
            {conversationHistory}
            Human: {message}
            AI: ";

            return promptText;
        }


        private async Task<string> CallGeminiApiAsync(string promptText)
        {
            try
            {
                var requestData = new
                {
                    contents = new[]
                    {
                        new
                        {
                            role = "user",
                            parts = new[]
                            {
                                new
                                {
                                    text = promptText
                                }
                            }
                        }
                    },
                    generationConfig = new
                    {
                        temperature = 0.7,
                        topK = 40,
                        topP = 0.95,
                        maxOutputTokens = 2048,
                        stopSequences = new string[] { }
                    },
                    safetySettings = new[]
                    {
                        new
                        {
                            category = "HARM_CATEGORY_DANGEROUS_CONTENT",
                            threshold = "BLOCK_MEDIUM_AND_ABOVE"
                        },
                        new
                        {
                            category = "HARM_CATEGORY_HATE_SPEECH",
                            threshold = "BLOCK_MEDIUM_AND_ABOVE"
                        },
                        new
                        {
                            category = "HARM_CATEGORY_HARASSMENT",
                            threshold = "BLOCK_MEDIUM_AND_ABOVE"
                        },
                        new
                        {
                            category = "HARM_CATEGORY_SEXUALLY_EXPLICIT",
                            threshold = "BLOCK_MEDIUM_AND_ABOVE"
                        }
                    }
                };

                var content = new StringContent(
                    JsonSerializer.Serialize(requestData),
                    Encoding.UTF8,
                    "application/json");

                // Make the API call (with API key as query parameter)
                var response = await _httpClient.PostAsync(
                    $"v1beta/models/{_modelName}:generateContent?key={_apiKey}",
                    content);

                response.EnsureSuccessStatusCode();

                var responseString = await response.Content.ReadAsStringAsync();
                var responseJson = JsonDocument.Parse(responseString);

                // Extract the response text
                var candidatesElement = responseJson.RootElement.GetProperty("candidates");
                if (candidatesElement.GetArrayLength() > 0)
                {
                    var contentElement = candidatesElement[0].GetProperty("content");
                    var partsElement = contentElement.GetProperty("parts");
                    if (partsElement.GetArrayLength() > 0)
                    {
                        return partsElement[0].GetProperty("text").GetString();
                    }
                }

                _logger.LogWarning("Unexpected response format from Gemini API");
                return "I'm sorry, I couldn't generate a response at this time.";
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "HTTP error calling Gemini API");

                if (ex.StatusCode != null)
                {
                    return ex.StatusCode switch
                    {
                        System.Net.HttpStatusCode.Unauthorized => "I'm sorry, the AI service cannot process your request due to an invalid API key.",
                        System.Net.HttpStatusCode.Forbidden => "I'm sorry, this API key doesn't have permission to use the requested model.",
                        System.Net.HttpStatusCode.TooManyRequests => "I'm sorry, the API usage quota has been exceeded. Please try again later.",
                        System.Net.HttpStatusCode.NotFound => $"I'm sorry, the specified model '{_modelName}' was not found.",
                        _ => "I'm sorry, I encountered a technical issue while processing your request."
                    };
                }

                return "I'm sorry, I couldn't connect to the AI service at this time.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error calling Gemini API");
                return "I'm sorry, I encountered an error processing your request.";
            }
        }

        private ProcessedResponse ProcessGeminiResponse(string response)
        {
            var processedResponse = new ProcessedResponse
            {
                Text = response,
                Format = "text" // Default format
            };

            if (string.IsNullOrEmpty(response))
            {
                _logger.LogWarning("Received empty response from Gemini API");
                processedResponse.Text = "I'm sorry, I couldn't generate a response at this time. Please try again later.";
                return processedResponse;
            }

            try
            {
                // Look for format tag: [format:type]
                var formatRegex = new Regex(@"\[format:(text|contact)\]", RegexOptions.IgnoreCase);
                var match = formatRegex.Match(response);

                if (match.Success)
                {
                    // Extract the format type
                    processedResponse.Format = match.Groups[1].Value.ToLower();

                    // Remove the format tag from the response
                    processedResponse.Text = formatRegex.Replace(response, "").Trim();
                }

                // Remove [/format] tag if present
                processedResponse.Text = processedResponse.Text.Replace("[/format]", "").Trim();

                // Look for JSON data in the response
                var jsonDataRegex = new Regex(@"\[data:([\s\S]*?)\]", RegexOptions.Singleline);
                var dataMatch = jsonDataRegex.Match(processedResponse.Text);

                if (dataMatch.Success)
                {
                    try
                    {
                        string jsonData = dataMatch.Groups[1].Value.Trim();
                        _logger.LogDebug($"Found JSON data: {jsonData.Substring(0, Math.Min(100, jsonData.Length))}...");


                        // Parse the JSON data
                        var options = new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true,
                            AllowTrailingCommas = true,
                            ReadCommentHandling = JsonCommentHandling.Skip
                        };

                        processedResponse.FormatData = JsonSerializer.Deserialize<object>(jsonData, options);

                        // Remove the data tag from the response text
                        processedResponse.Text = jsonDataRegex.Replace(processedResponse.Text, "").Trim();
                    }
                    catch (JsonException ex)
                    {
                        _logger.LogWarning(ex, "Failed to parse JSON data");

                        // Create fallback data
                        processedResponse.FormatData = CreateFallbackData(processedResponse.Format);

                        // Remove the problematic data tag
                        processedResponse.Text = jsonDataRegex.Replace(processedResponse.Text, "").Trim();

                        // Add a note if one doesn't already exist
                        if (!processedResponse.Text.Contains("using default") && !processedResponse.Text.Contains("default template"))
                        {
                            processedResponse.Text += "\n\nNote: There was an issue with the data format. Using default data.";
                        }
                    }
                }
                else if (processedResponse.Format != "text")
                {
                    // If a special format is requested but no data is provided, create fallback data
                    _logger.LogWarning($"No JSON data found for format type: {processedResponse.Format}");
                    processedResponse.FormatData = CreateFallbackData(processedResponse.Format);

                    // Add a note if one doesn't already exist
                    if (!processedResponse.Text.Contains("using default") && !processedResponse.Text.Contains("default template"))
                    {
                        processedResponse.Text += "\n\nNote: No structured data was provided. Using default template.";
                    }
                }

                return processedResponse;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing Gemini response");
                return new ProcessedResponse
                {
                    Text = "I encountered an error processing the response. " + response,
                    Format = "text"
                };
            }
        }

 

        private string CreateFallbackDataJson(string formatType)
        {
            string fallbackJson = "{}";

            switch (formatType.ToLower())
            {

                case "contact":
                    fallbackJson = "{\"title\":\"Contact Form\",\"recipientName\":\"Portfolio Owner\",\"recipientPosition\":\"Full Stack Developer\",\"emailSubject\":\"Contact from Portfolio Website\",\"socialLinks\":[{\"platform\":\"LinkedIn\",\"url\":\"#\",\"icon\":\"linkedin\"}]}";
                    break;
            }

            return fallbackJson;
        }

        private object CreateFallbackData(string formatType)
        {
            switch (formatType.ToLower())
            {
 

                case "contact":
                    return new
                    {
                        title = "Contact Form",
                        recipientName = "Portfolio Owner",
                        recipientPosition = "Full Stack Developer",
                        emailSubject = "Contact from Portfolio Website",
                        socialLinks = new[]
                        {
                            new { platform = "LinkedIn", url = "#", icon = "linkedin" }
                        }
                    };

                default:
                    return null;
            }
        }

        private string GetStyleInstructions(string style)
        {
            var styleInstructions = new Dictionary<string, string>
            {
                ["NORMAL"] = "Use a friendly, conversational tone with a balanced amount of detail. Be professional but approachable. Use everyday language that's easy to understand. Structure your responses in a clear, logical way with paragraphs for different points. Also respond in the language he talks to you in.",

                ["FORMAL"] = "Use a professional, academic tone with precise language and terminology. Maintain a respectful distance with minimal use of contractions or colloquialisms. Structure your responses with clear introductions, well-defined sections, and formal conclusions. Use industry-standard terminology when appropriate. Also respond in the language he talks to you in.",

                ["EXPLANATORY"] = "Focus on education and clarity. Break down complex concepts into manageable parts. Use analogies, examples, and step-by-step explanations. Define any technical terms you use. Structure your response with headings, numbered lists for sequences, and bullet points for key takeaways. Also respond in the language he talks to you in.",

                ["MINIMALIST"] = "Be concise and direct. Prioritize brevity over comprehensiveness. Use short sentences and paragraphs. Avoid unnecessary elaboration. Focus only on the most essential information. Use bullet points when appropriate. Eliminate redundancy. Also respond in the language he talks to you in.",

                ["HR"] = "Use a professional yet empathetic tone suitable for human resources communications. Be clear and specific about qualifications, skills, and experience. Balance professionalism with approachability. Use industry-standard HR terminology. Maintain a positive, encouraging tone while being realistic and honest. Also respond in the language he talks to you in."
            };

            return styleInstructions.TryGetValue(style, out var instructions)
                ? instructions
                : styleInstructions["NORMAL"];
        }
    }
}