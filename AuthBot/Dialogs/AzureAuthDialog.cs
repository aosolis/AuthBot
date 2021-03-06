﻿// Copyright (c) Microsoft. All rights reserved. Licensed under the MIT license. See full license at the bottom of this file.
namespace AuthBot.Dialogs
{
    using System;
    using System.Threading.Tasks;
    using Helpers;
    using Microsoft.Bot.Builder.Dialogs;
    using Microsoft.Bot.Connector;
    using Models;
    using Microsoft.Bot.Builder.Dialogs.Internals;
    using Autofac;
    using System.Collections.Generic;
    using System.Text.RegularExpressions;
    using System.Globalization;

    [Serializable]
    public class AzureAuthDialog : IDialog<string>
    {
        private Regex MagicNumberRegex = new Regex(@"\b(\d{6})\b", RegexOptions.Singleline);

        protected string resourceId { get; }
        protected string[] scopes { get; }
        protected string prompt { get; }



        public AzureAuthDialog(string resourceId, string prompt = "Please click to sign in: ")
        {
            this.resourceId = resourceId;
            this.prompt = prompt;
        }
        public AzureAuthDialog(string[] scopes, string prompt = "Please click to sign in: ")
        {
            this.scopes = scopes;
            this.prompt = prompt;
        }


        public async Task StartAsync(IDialogContext context)
        {
            context.Wait(this.MessageReceivedAsync);
        }

        public virtual async Task MessageReceivedAsync(IDialogContext context, IAwaitable<IMessageActivity> argument)
        {
            var msg = await argument;

            AuthResult authResult;
            string validated = "";
            int magicNumber = 0;
            if (context.UserData.TryGetValue(ContextConstants.AuthResultKey, out authResult))
            {
                try
                {
                    //IMPORTANT: DO NOT REMOVE THE MAGIC NUMBER CHECK THAT WE DO HERE. THIS IS AN ABSOLUTE SECURITY REQUIREMENT
                    //REMOVING THIS WILL REMOVE YOUR BOT AND YOUR USERS TO SECURITY VULNERABILITIES. 
                    //MAKE SURE YOU UNDERSTAND THE ATTACK VECTORS AND WHY THIS IS IN PLACE.
                    context.UserData.TryGetValue<string>(ContextConstants.MagicNumberValidated, out validated);
                    if (validated == "true")
                    {
                        context.Done($"Thanks {authResult.UserName}. You are now logged in. ");
                    }
                    else if (context.UserData.TryGetValue<int>(ContextConstants.MagicNumberKey, out magicNumber))
                    {
                        if (msg.Text == null)
                        {
                            await context.PostAsync($"Please paste back the number you received in your authentication screen.");

                            context.Wait(this.MessageReceivedAsync);
                        }
                        else
                        {
                            var match = MagicNumberRegex.Match(msg.Text);
                            if (match.Success && (magicNumber.ToString(CultureInfo.InvariantCulture) == match.Groups[1].Value))
                            {
                                context.UserData.SetValue<string>(ContextConstants.MagicNumberValidated, "true");
                                context.Done($"Thanks {authResult.UserName}. You are now logged in. ");
                            }
                            else
                            {
                                context.UserData.RemoveValue(ContextConstants.AuthResultKey);
                                context.UserData.SetValue<string>(ContextConstants.MagicNumberValidated, "false");
                                context.UserData.RemoveValue(ContextConstants.MagicNumberKey);
                                await context.PostAsync($"I'm sorry but I couldn't validate your number. Please try authenticating once again. ");

                                context.Wait(this.MessageReceivedAsync);
                            }
                        }
                    }
                }
                catch
                {
                    context.UserData.RemoveValue(ContextConstants.AuthResultKey);
                    context.UserData.SetValue(ContextConstants.MagicNumberValidated, "false");
                    context.UserData.RemoveValue(ContextConstants.MagicNumberKey);
                    context.Done($"I'm sorry but something went wrong while authenticating.");
                }
            }
            else
            {
                await this.CheckForLogin(context, msg);
            }
        }

        /// <summary>
        /// Prompts the user to login. This can be overridden inorder to allow custom prompt messages or cards per channel.
        /// </summary>
        /// <param name="context">Chat context</param>
        /// <param name="msg">Chat message</param>
        /// <param name="authenticationUrl">OAuth URL for authenticating user</param>
        /// <returns>Task from Posting or prompt to the context.</returns>
        protected virtual Task PromptToLogin(IDialogContext context, IMessageActivity msg, string authenticationUrl)
        {
            Attachment plAttachment = null;
            switch (msg.ChannelId)
            {
                case "skypeforbusiness":
                    return context.PostAsync(this.prompt + "[Click here](" + authenticationUrl + ")");
                case "emulator":
                case "skype":
                    {
                        SigninCard plCard = new SigninCard(this.prompt, GetCardActions(authenticationUrl, "signin"));
                        plAttachment = plCard.ToAttachment();
                        break;
                    }
                // Teams does not yet support signin cards
                case "msteams":
                    {
                        ThumbnailCard plCard = new ThumbnailCard()
                        {
                            Title = this.prompt,
                            Subtitle = "",
                            Images = new List<CardImage>(),
                            Buttons = GetCardActions(authenticationUrl, "openUrl")
                        };
                        plAttachment = plCard.ToAttachment();
                        break;
                    }
                default:
                    {
                        SigninCard plCard = new SigninCard(this.prompt, GetCardActions(authenticationUrl, "signin"));
                        plAttachment = plCard.ToAttachment();
                        break;
                    }
//                    return context.PostAsync(this.prompt + "[Click here](" + authenticationUrl + ")");
            }

            IMessageActivity response = context.MakeMessage();
            response.Recipient = msg.From;
            response.Type = "message";

            response.Attachments = new List<Attachment>();
            response.Attachments.Add(plAttachment);

            return context.PostAsync(response);
        }

        private List<CardAction> GetCardActions(string authenticationUrl, string actionType)
        {
            List<CardAction> cardButtons = new List<CardAction>();
            CardAction plButton = new CardAction()
            {
                Value = authenticationUrl,
                Type = actionType,
                Title = "Authentication Required"
            };
            cardButtons.Add(plButton);
            return cardButtons;
        }

        /// <summary>
        /// Checks if we are able to get an access token. If not, we prompt for a login
        /// </summary>
        /// <param name="context"></param>
        /// <param name="msg"></param>
        /// <returns></returns>
        protected virtual async Task CheckForLogin(IDialogContext context, IMessageActivity msg)
        {
            try
            {
                string token;
                if (resourceId != null)
                    token = await context.GetAccessToken(resourceId);
                else
                    token = await context.GetAccessToken(scopes);

                if (string.IsNullOrEmpty(token))
                {
                    if (msg.Text != null &&
                        CancellationWords.GetCancellationWords().Contains(msg.Text.ToUpper()))
                    {
                        context.Done(string.Empty);
                    }
                    else
                    {
                        var conversationReference = new ConversationReference(msg.Id, msg.From, msg.Recipient, msg.Conversation, msg.ChannelId, msg.ServiceUrl);

                        string authenticationUrl;
                        if (resourceId != null)
                            authenticationUrl = await AzureActiveDirectoryHelper.GetAuthUrlAsync(conversationReference, resourceId);
                        else
                            authenticationUrl = await AzureActiveDirectoryHelper.GetAuthUrlAsync(conversationReference, scopes);

                        await PromptToLogin(context, msg, authenticationUrl);
                        context.Wait(this.MessageReceivedAsync);
                    }
                }
                else
                {
                    context.Done(string.Empty);
                }
            }
            catch (Exception ex)
            {
                throw ex;
            }
        }
    }
}


//*********************************************************
//
//AuthBot, https://github.com/microsoftdx/AuthBot
//
//Copyright (c) Microsoft Corporation
//All rights reserved.
//
// MIT License:
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// ""Software""), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
//
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED ""AS IS"", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//
//*********************************************************
