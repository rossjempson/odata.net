﻿//---------------------------------------------------------------------
// <copyright file="CreateHandler.cs" company="Microsoft">
//      Copyright (C) Microsoft Corporation. All rights reserved. See License.txt in the project root for license information.
// </copyright>
//---------------------------------------------------------------------

namespace Microsoft.Test.Taupo.OData.WCFService
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using Microsoft.OData.Edm;
    using Microsoft.OData.Core;

    /// <summary>
    /// This class is responsible for handling requests to insert new entities.
    /// </summary>
    public class CreateHandler : RequestHandler
    {
        /// <summary>
        /// Parses the request URI and message body to create a new entry in the data store.
        /// </summary>
        /// <param name="messageBody">Stream containing the new entry to create.</param>
        /// <returns>Stream containing the new entry if successful, otherwise an error.</returns>
        public Stream ProcessCreateRequest(Stream messageBody)
        {
            object result;
            IEdmEntitySet targetEntitySet;

            try
            {
                var queryContext = this.GetDefaultQueryContext();
                targetEntitySet = queryContext.ResolveEntitySet();
                IncomingRequestMessage message = this.GetIncomingRequestMessage(messageBody);

                result = ProcessPostBody(message, targetEntitySet);
            }
            catch (Exception error)
            {
                return this.WriteErrorResponse(400, error);
            }

            return this.WriteResponse(
                200,
                (writer, writerSettings, responseMessage) =>
                {
                    ODataVersion targetVersion = writerSettings.Version.GetValueOrDefault();
                    responseMessage.SetHeader("Location", ODataObjectModelConverter.BuildEntryUri(result, targetEntitySet, targetVersion).OriginalString);
                    ResponseWriter.WriteEntry(writer.CreateODataEntryWriter(targetEntitySet), result, targetEntitySet, this.Model, targetVersion, Enumerable.Empty<string>());
                });
        }

        private object ProcessPostBody(IncomingRequestMessage message, IEdmEntitySet entitySet)
        {
            object lastNewInstance = null;

            using (var messageReader = new ODataMessageReader(message, this.GetDefaultReaderSettings(), this.Model))
            {
                var odataItemStack = new Stack<ODataItem>();
                var entryReader = messageReader.CreateODataEntryReader(entitySet.EntityType());
                IEdmEntitySet currentTargetEntitySet = entitySet;

                while (entryReader.Read())
                {
                    switch (entryReader.State)
                    {
                        case ODataReaderState.EntryStart:
                            entryReader.Item.SetAnnotation(new TargetEntitySetAnnotation { TargetEntitySet = currentTargetEntitySet });
                            odataItemStack.Push(entryReader.Item);
                            break;

                        case ODataReaderState.EntryEnd:
                            {
                                var entry = (ODataEntry)entryReader.Item;

                                var targetEntitySet = entry.GetAnnotation<TargetEntitySetAnnotation>().TargetEntitySet;
                                object newInstance = this.DataContext.CreateNewItem(targetEntitySet);

                                foreach (var property in entry.Properties)
                                {
                                    DataContext.UpdatePropertyValue(newInstance, property.Name, property.Value);
                                }

                                var boundNavPropAnnotation = odataItemStack.Pop().GetAnnotation<BoundNavigationPropertyAnnotation>();
                                if (boundNavPropAnnotation != null)
                                {
                                    foreach (var boundProperty in boundNavPropAnnotation.BoundProperties)
                                    {
                                        bool isCollection = boundProperty.Item1.IsCollection == true;
                                        object propertyValue = isCollection ? boundProperty.Item2 : ((IEnumerable<object>)boundProperty.Item2).Single();
                                        DataContext.UpdatePropertyValue(newInstance, boundProperty.Item1.Name, propertyValue);
                                    }
                                }

                                var parentItem = odataItemStack.Count > 0 ? odataItemStack.Peek() : null;
                                if (parentItem != null)
                                {
                                    // This new entry belongs to a navigation property and/or feed -
                                    // propagate it up the tree for further processing.
                                    AddChildInstanceAnnotation(parentItem, newInstance);
                                }

                                this.DataContext.AddItem(targetEntitySet, newInstance);
                                lastNewInstance = newInstance;
                            }

                            break;

                        case ODataReaderState.FeedStart:
                            odataItemStack.Push(entryReader.Item);
                            break;

                        case ODataReaderState.FeedEnd:
                            {
                                var childAnnotation = odataItemStack.Pop().GetAnnotation<ChildInstanceAnnotation>();

                                var parentNavLink = odataItemStack.Count > 0 ? odataItemStack.Peek() as ODataNavigationLink : null;
                                if (parentNavLink != null)
                                {
                                    // This feed belongs to a navigation property -
                                    // propagate it up the tree for further processing.
                                    AddChildInstanceAnnotation(parentNavLink, childAnnotation.ChildInstances ?? new object[0]);
                                }
                            }

                            break;

                        case ODataReaderState.NavigationLinkStart:
                            {
                                odataItemStack.Push(entryReader.Item);
                                var navigationLink = (ODataNavigationLink)entryReader.Item;
                                var navigationProperty = (IEdmNavigationProperty)currentTargetEntitySet.EntityType().FindProperty(navigationLink.Name);

                                // Current model implementation doesn't expose associations otherwise this would be much cleaner.
                                currentTargetEntitySet = this.Model.EntityContainer.EntitySets().Single(s => s.EntityType() == navigationProperty.Type.Definition);
                            }

                            break;

                        case ODataReaderState.NavigationLinkEnd:
                            {
                                var navigationLink = (ODataNavigationLink)entryReader.Item;
                                var childAnnotation = odataItemStack.Pop().GetAnnotation<ChildInstanceAnnotation>();
                                if (childAnnotation != null)
                                {
                                    // Propagate the bound entries to the parent entry.
                                    AddBoundNavigationPropertyAnnotation(odataItemStack.Peek(), navigationLink, childAnnotation.ChildInstances);
                                }
                            }

                            break;
                    }
                }
            }

            return lastNewInstance;
        }

        private void AddBoundNavigationPropertyAnnotation(ODataItem item, ODataNavigationLink navigationLink, object boundValue)
        {
            var annotation = item.GetAnnotation<BoundNavigationPropertyAnnotation>();
            if (annotation == null)
            { 
                annotation = new BoundNavigationPropertyAnnotation { BoundProperties = new List<Tuple<ODataNavigationLink, object>>() };
                item.SetAnnotation(annotation);
            }

            annotation.BoundProperties.Add(new Tuple<ODataNavigationLink, object>(navigationLink, boundValue));
        }

        private void AddChildInstanceAnnotation(ODataItem item, object childEntry)
        {
            var annotation = item.GetAnnotation<ChildInstanceAnnotation>();
            if (annotation == null)
            { 
                annotation = new ChildInstanceAnnotation { ChildInstances = new List<object>() };
                item.SetAnnotation(annotation);
            }

            annotation.ChildInstances.Add(childEntry);
        }

        /// <summary>
        /// Annotation for marking a new entry with bound associated entries.
        /// </summary>
        private class BoundNavigationPropertyAnnotation
        {
            public IList<Tuple<ODataNavigationLink, object>> BoundProperties { get; set; } 
        }

        /// <summary>
        /// Annotation for marking a navigation property or feed with new entry instances belonging to it.
        /// </summary>
        private class ChildInstanceAnnotation
        {
            public IList<object> ChildInstances { get; set; } 
        }

        /// <summary>
        /// Annotation for marking an entry with the entity set that it belongs to.
        /// </summary>
        private class TargetEntitySetAnnotation
        {
            public IEdmEntitySet TargetEntitySet { get; set; }
        }
    }
}
