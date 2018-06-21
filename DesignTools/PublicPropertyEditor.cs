﻿/*
Copyright (c) 2018, Lars Brubaker, John Lewin
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer.
2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR
ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

The views and conclusions contained in the software and documentation are those
of the authors and should not be interpreted as representing official policies,
either expressed or implied, of the FreeBSD Project.
*/

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Threading;
using MatterHackers.Agg;
using MatterHackers.Agg.Image;
using MatterHackers.Agg.Platform;
using MatterHackers.Agg.UI;
using MatterHackers.DataConverters3D;
using MatterHackers.Localizations;
using MatterHackers.MatterControl.CustomWidgets;
using MatterHackers.MatterControl.DesignTools.EditableTypes;
using MatterHackers.MatterControl.PartPreviewWindow;
using MatterHackers.MatterControl.PartPreviewWindow.View3D;
using MatterHackers.MatterControl.SlicerConfiguration;
using MatterHackers.VectorMath;

namespace MatterHackers.MatterControl.DesignTools
{
	public class EditableProperty
	{
		public IObject3D Item { get; private set; }

		public object source;
		public PropertyInfo PropertyInfo { get; private set; }

		public EditableProperty(PropertyInfo p, object source)
		{
			this.source = source;
			this.Item = source as IObject3D;
			this.PropertyInfo = p;
		}

		private string GetDescription(PropertyInfo prop)
		{
			var nameAttribute = prop.GetCustomAttributes(true).OfType<DescriptionAttribute>().FirstOrDefault();
			return nameAttribute?.Description ?? null;
		}

		public static string GetDisplayName(PropertyInfo prop)
		{
			var nameAttribute = prop.GetCustomAttributes(true).OfType<DisplayNameAttribute>().FirstOrDefault();
			return nameAttribute?.DisplayName ?? prop.Name.SplitCamelCase();
		}

		public object Value => PropertyInfo.GetGetMethod().Invoke(source, null);

		/// <summary>
		/// Use reflection to set property value
		/// </summary>
		/// <param name="value"></param>
		public void SetValue(object value)
		{
			this.PropertyInfo.GetSetMethod().Invoke(source, new Object[] { value });
		}

		public string DisplayName => GetDisplayName(PropertyInfo);
		public string Description => GetDescription(PropertyInfo);
		public Type PropertyType => PropertyInfo.PropertyType;
	}

	public class PublicPropertyEditor : IObject3DEditor
	{
		public string Name => "Property Editor";

		public bool Unlocked { get; } = true;

		public IEnumerable<Type> SupportedTypes() => new Type[] { typeof(IObject3D) };

		private static Type[] allowedTypes =
		{
			typeof(double), typeof(int), typeof(char), typeof(string), typeof(bool),
			typeof(Vector2), typeof(Vector3),
			typeof(DirectionVector), typeof(DirectionAxis),
			typeof(ChildrenSelector),
			typeof(ImageBuffer),
		};

		public const BindingFlags OwnedPropertiesOnly = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;

		public GuiWidget Create(IObject3D item, ThemeConfig theme)
		{
			var mainContainer = new FlowLayoutWidget(FlowDirection.TopToBottom)
			{
				HAnchor = HAnchor.Stretch
			};

			// TODO: Long term we should have a solution where editors can extend Draw and Undo without this hack
			var view3DWidget = ApplicationController.Instance.DragDropData.View3DWidget;
			var undoBuffer = view3DWidget.sceneContext.Scene.UndoBuffer;

			if (item is IEditorDraw editorDraw)
			{
				// TODO: Putting the drawing code in the IObject3D means almost certain bindings to MatterControl in IObject3D. If instead
				// we had a UI layer object that used binding to register scene drawing hooks for specific types, we could avoid the bindings
				view3DWidget.InteractionLayer.DrawGlOpaqueContent += editorDraw.DrawEditor;
				mainContainer.Closed += (s, e) =>
				{
					view3DWidget.InteractionLayer.DrawGlOpaqueContent -= editorDraw.DrawEditor;
				};
			}

			if (item != null)
			{
				var context = new PPEContext()
				{
					item = item
				};

				// CreateEditor
				AddWebPageLinkIfRequired(context, mainContainer, theme);
				AddUnlockLinkIfRequired(context, mainContainer, theme);

				// Create a field editor for each editable property detected via reflection
				foreach (var property in GetEditablePropreties(context.item))
				{
					var editor = CreatePropertyEditor(property, undoBuffer, context, theme);
					if (editor != null)
					{
						mainContainer.AddChild(editor);
					}
				}

				// add in an Update button if applicable
				var showUpdate = context.item.GetType().GetCustomAttributes(typeof(ShowUpdateButtonAttribute), true).FirstOrDefault() as ShowUpdateButtonAttribute;
				if (showUpdate != null)
				{
					var updateButton = theme.ButtonFactory.Generate("Update".Localize());
					updateButton.Margin = new BorderDouble(5);
					updateButton.HAnchor = HAnchor.Right;
					updateButton.Click += (s, e) =>
					{
						context.item.Invalidate(new InvalidateArgs(context.item, InvalidateType.Properties, undoBuffer));
					};
					mainContainer.AddChild(updateButton);
				}

				// Init with custom 'UpdateControls' hooks
				(context.item as IPropertyGridModifier)?.UpdateControls(context);
			}

			return mainContainer;
		}

		private static FlowLayoutWidget CreateSettingsRow(EditableProperty property, UIField field)
		{
			var row = CreateSettingsRow(property.DisplayName.Localize(), property.Description.Localize());
			row.AddChild(field.Content);

			return row;
		}

		private static FlowLayoutWidget CreateSettingsRow(string labelText, string toolTipText = null)
		{
			var rowContainer = new FlowLayoutWidget(FlowDirection.LeftToRight)
			{
				HAnchor = HAnchor.Stretch,
				Padding = new BorderDouble(5),
				ToolTipText = toolTipText
			};

			var label = new TextWidget(labelText + ":", pointSize: 11, textColor: ActiveTheme.Instance.PrimaryTextColor)
			{
				Margin = new BorderDouble(0, 0, 3, 0),
				VAnchor = VAnchor.Center
			};
			rowContainer.AddChild(label);

			rowContainer.AddChild(new HorizontalSpacer());

			return rowContainer;
		}

		private static FlowLayoutWidget CreateSettingsColumn(EditableProperty property)
		{
			return CreateSettingsColumn(property.DisplayName.Localize(), property.Description.Localize());
		}

		private static FlowLayoutWidget CreateSettingsColumn(string labelText, string toolTipText = null)
		{
			var columnContainer = new FlowLayoutWidget(FlowDirection.TopToBottom)
			{
				HAnchor = HAnchor.Stretch,
				Padding = new BorderDouble(5),
				ToolTipText = toolTipText
			};

			var label = new TextWidget(labelText + ":", pointSize: 11, textColor: ActiveTheme.Instance.PrimaryTextColor)
			{
				Margin = new BorderDouble(0, 3, 0, 0),
				HAnchor = HAnchor.Left
			};
			columnContainer.AddChild(label);

			return columnContainer;
		}

		public static IEnumerable<EditableProperty> GetEditablePropreties(IObject3D item)
		{
			return item.GetType().GetProperties(OwnedPropertiesOnly)
				.Where(pi => (allowedTypes.Contains(pi.PropertyType) || pi.PropertyType.IsEnum)
					&& pi.GetGetMethod() != null
					&& pi.GetSetMethod() != null)
				.Select(p => new EditableProperty(p, item));
		}

		public static GuiWidget CreatePropertyEditor(EditableProperty property, UndoBuffer undoBuffer, PPEContext context, ThemeConfig theme)
		{
			var object3D = property.Item;
			var propertyGridModifier = property.Item as IPropertyGridModifier;

			GuiWidget rowContainer = null;

			// Get reflected property value once, then test for each case below
			var propertyValue = property.Value;

			// create a double editor
			if (propertyValue is double doubleValue)
			{
				var field = new DoubleField();
				field.Initialize(0);
				field.DoubleValue = doubleValue;
				field.ValueChanged += (s, e) =>
				{
					property.SetValue(field.DoubleValue);
					object3D?.Invalidate(new InvalidateArgs(context.item, InvalidateType.Properties, undoBuffer));
					propertyGridModifier?.UpdateControls(context);
				};

				void RefreshField(object s, InvalidateArgs e)
				{
					if (e.InvalidateType == InvalidateType.Properties)
					{
						double newValue = (double)property.Value;
						if (newValue != field.DoubleValue)
						{
							field.DoubleValue = newValue;
						}
					}
				}

				object3D.Invalidated += RefreshField;
				field.Content.Closed += (s, e) => object3D.Invalidated -= RefreshField;

				rowContainer = CreateSettingsRow(property, field);
			}
			else if (propertyValue is Vector2 vector2)
			{
				var field = new Vector2Field();
				field.Initialize(0);
				field.Vector2 = vector2;
				field.ValueChanged += (s, e) =>
				{
					property.SetValue(field.Vector2);
					object3D?.Invalidate(new InvalidateArgs(context.item, InvalidateType.Properties, undoBuffer));
					propertyGridModifier?.UpdateControls(context);
				};

				rowContainer = CreateSettingsRow(property, field);
			}
			else if (propertyValue is Vector3 vector3)
			{
				var field = new Vector3Field();
				field.Initialize(0);
				field.Vector3 = vector3;
				field.ValueChanged += (s, e) =>
				{
					property.SetValue(field.Vector3);
					object3D?.Invalidate(new InvalidateArgs(context.item, InvalidateType.Properties, undoBuffer));
					propertyGridModifier?.UpdateControls(context);
				};

				rowContainer = CreateSettingsRow(property, field);
			}
			else if (propertyValue is DirectionVector directionVector)
			{
				var field = new DirectionVectorField();
				field.Initialize(0);
				field.SetValue(directionVector);
				field.ValueChanged += (s, e) =>
				{
					property.SetValue(field.DirectionVector);
					object3D?.Invalidate(new InvalidateArgs(context.item, InvalidateType.Properties, undoBuffer));
					propertyGridModifier?.UpdateControls(context);
				};

				rowContainer = CreateSettingsRow(property, field);
			}
			else if (propertyValue is DirectionAxis directionAxis)
			{
				// the direction axis
				// the distance from the center of the part
				// create a double editor
				var field = new DoubleField();
				field.Initialize(0);
				field.DoubleValue = directionAxis.Origin.X - property.Item.Children.First().GetAxisAlignedBoundingBox().Center.X;
				field.ValueChanged += (s, e) =>
				{
					property.SetValue(
						new DirectionAxis()
						{
							Normal = Vector3.UnitZ, Origin = property.Item.Children.First().GetAxisAlignedBoundingBox().Center + new Vector3(field.DoubleValue, 0, 0)
						});
					object3D?.Invalidate(new InvalidateArgs(context.item, InvalidateType.Properties, undoBuffer));
					propertyGridModifier?.UpdateControls(context);
				};

				// update this when changed
				EventHandler<InvalidateArgs> updateData = (s, e) =>
				{
					field.DoubleValue = ((DirectionAxis)property.Value).Origin.X - property.Item.Children.First().GetAxisAlignedBoundingBox().Center.X;
				};
				property.Item.Invalidated += updateData;
				field.Content.Closed += (s, e) =>
				{
					property.Item.Invalidated -= updateData;
				};

				rowContainer = CreateSettingsRow(property, field);
			}
			else if (propertyValue is ChildrenSelector childSelector)
			{
				rowContainer = CreateSettingsColumn(property);
				rowContainer.AddChild(CreateSelector(childSelector, property.Item, theme));
			}
			else if (propertyValue is ImageBuffer imageBuffer)
			{
				rowContainer = CreateSettingsColumn(property);
				rowContainer.AddChild(CreateImageDisplay(imageBuffer, property.Item, theme));
			}
			// create a int editor
			else if (propertyValue is int intValue)
			{
				var field = new IntField();
				field.Initialize(0);
				field.IntValue = intValue;
				field.ValueChanged += (s, e) =>
				{
					property.SetValue(field.IntValue);
					object3D?.Invalidate(new InvalidateArgs(context.item, InvalidateType.Properties, undoBuffer));
					propertyGridModifier?.UpdateControls(context);
				};

				rowContainer = CreateSettingsRow(property, field);
			}
			// create a bool editor
			else if (propertyValue is bool boolValue)
			{
				var field = new ToggleboxField(theme);
				field.Initialize(0);
				field.Checked = boolValue;
				field.ValueChanged += (s, e) =>
				{
					property.SetValue(field.Checked);
					object3D?.Invalidate(new InvalidateArgs(context.item, InvalidateType.Properties, undoBuffer));
					propertyGridModifier?.UpdateControls(context);
				};

				rowContainer = CreateSettingsRow(property, field);
			}
			// create a string editor
			else if (propertyValue is string stringValue)
			{
				var field = new TextField();
				field.Initialize(0);
				field.SetValue(stringValue, false);
				field.ValueChanged += (s, e) =>
				{
					property.SetValue(field.Value);
					object3D?.Invalidate(new InvalidateArgs(context.item, InvalidateType.Properties, undoBuffer));
					propertyGridModifier?.UpdateControls(context);
				};

				rowContainer = CreateSettingsRow(property, field);
			}
			// create a char editor
			else if (propertyValue is char charValue)
			{
				var field = new CharField();
				field.Initialize(0);
				field.SetValue(charValue.ToString(), false);
				field.ValueChanged += (s, e) =>
				{
					property.SetValue(Convert.ToChar(field.Value));
					object3D?.Invalidate(new InvalidateArgs(context.item, InvalidateType.Properties, undoBuffer));
					propertyGridModifier?.UpdateControls(context);
				};

				rowContainer = CreateSettingsRow(property, field);
			}
			// create an enum editor
			else if (property.PropertyType.IsEnum)
			{
				UIField field;
				var iconsAttribute = property.PropertyInfo.GetCustomAttributes(true).OfType<IconsAttribute>().FirstOrDefault();
				if (iconsAttribute != null)
				{
					field = new IconEnumField(property, iconsAttribute)
					{
						InitialValue = propertyValue.ToString()
					};
				}
				else
				{
					field = new EnumField(property);
				}

				field.Initialize(0);
				field.ValueChanged += (s, e) =>
				{
					property.SetValue(Enum.Parse(property.PropertyType, field.Value));
					object3D?.Invalidate(new InvalidateArgs(context.item, InvalidateType.Properties, undoBuffer));
					propertyGridModifier?.UpdateControls(context);
				};

				rowContainer = CreateSettingsRow(property, field);
			}
			// Use known IObject3D editors
			else if (propertyValue is IObject3D item
				&& ApplicationController.Instance.GetEditorsForType(property.PropertyType)?.FirstOrDefault() is IObject3DEditor iObject3DEditor)
			{
				rowContainer = iObject3DEditor.Create(item, theme);
			}

			// remember the row name and widget
			context.editRows.Add(property.PropertyInfo.Name, rowContainer);

			return rowContainer;
		}

		private static GuiWidget CreateImageDisplay(ImageBuffer imageBuffer, IObject3D parent, ThemeConfig theme)
		{
			return new ImageWidget(imageBuffer);
		}

		private static GuiWidget CreateSelector(ChildrenSelector childSelector, IObject3D parent, ThemeConfig theme)
		{
			GuiWidget tabContainer = new FlowLayoutWidget(FlowDirection.TopToBottom);

			void UpdateSelectColors(bool selectionChanged = false)
			{
				foreach (var child in parent.Children.ToList())
				{
					using (child.RebuildLock())
					{
						if (!childSelector.Contains(child.ID)
							|| tabContainer.HasBeenClosed)
						{
							child.Color = new Color(child.WorldColor(), 255);
						}
						else
						{
							child.Color = new Color(child.WorldColor(), 200);
						}

						if (selectionChanged)
						{
							child.Visible = true;
						}
					}
				}
			}

			tabContainer.Closed += (s, e) => UpdateSelectColors();

			var children = parent.Children.ToList();

			Dictionary<ICheckbox, IObject3D> objectChecks = new Dictionary<ICheckbox, IObject3D>();

			List<GuiWidget> radioSiblings = new List<GuiWidget>();
			for (int i = 0; i < children.Count; i++)
			{
				var itemIndex = i;
				var child = children[itemIndex];
				FlowLayoutWidget rowContainer = new FlowLayoutWidget();

				GuiWidget selectWidget;
				if (children.Count == 2)
				{
					var radioButton = new RadioButton(string.IsNullOrWhiteSpace(child.Name) ? $"{itemIndex}" : $"{child.Name}")
					{
						Checked = childSelector.Contains(child.ID),
						TextColor = ActiveTheme.Instance.PrimaryTextColor
					};
					radioSiblings.Add(radioButton);
					radioButton.SiblingRadioButtonList = radioSiblings;
					selectWidget = radioButton;
				}
				else
				{
					selectWidget = new CheckBox(string.IsNullOrWhiteSpace(child.Name) ? $"{itemIndex}" : $"{child.Name}")
					{
						Checked = childSelector.Contains(child.ID),
						TextColor = ActiveTheme.Instance.PrimaryTextColor
					};
				}

				objectChecks.Add((ICheckbox)selectWidget, child);

				rowContainer.AddChild(selectWidget);
				ICheckbox checkBox = selectWidget as ICheckbox;

				checkBox.CheckedStateChanged += (s, e) =>
				{
					if (s is ICheckbox checkbox)
					{
						if (checkBox.Checked)
						{
							if (!childSelector.Contains(objectChecks[checkbox].ID))
							{
								childSelector.Add(objectChecks[checkbox].ID);
							}
						}
						else
						{
							if (childSelector.Contains(objectChecks[checkbox].ID))
							{
								childSelector.Remove(objectChecks[checkbox].ID);
							}
						}

						if(parent is MeshWrapperObject3D meshWrapper)
						{
							using (meshWrapper.RebuildLock())
							{
								meshWrapper.ResetMeshWrapperMeshes(Object3DPropertyFlags.All, CancellationToken.None);
							}
						}

						UpdateSelectColors(true);
					}
				};

				tabContainer.AddChild(rowContainer);
				UpdateSelectColors();
			}

			/*
			bool operationApplied = parent.Descendants()
				.Where((obj) => obj.OwnerID == parent.ID)
				.Where((objId) => objId.Mesh != objId.Children.First().Mesh).Any();

			bool selectionHasBeenMade = parent.Descendants()
				.Where((obj) => obj.OwnerID == parent.ID && obj.OutputType == PrintOutputTypes.Hole)
				.Any();

			if (!operationApplied && !selectionHasBeenMade)
			{
				// select the last item
				if (tabContainer.Descendants().Where((d) => d is ICheckbox).Last() is ICheckbox lastCheckBox)
				{
					lastCheckBox.Checked = true;
				}
			}
			else
			{
				updateButton.Enabled = !operationApplied;
			}
			*/

			return tabContainer;
		}

		private void AddUnlockLinkIfRequired(PPEContext context, FlowLayoutWidget editControlsContainer, ThemeConfig theme)
		{
			var unlockLink = context.item.GetType().GetCustomAttributes(typeof(UnlockLinkAttribute), true).FirstOrDefault() as UnlockLinkAttribute;
			if (unlockLink != null
				&& !string.IsNullOrEmpty(unlockLink.UnlockPageLink)
				&& !context.item.Persistable)
			{
				var row = CreateSettingsRow(context.item.Persistable ? "Registered".Localize() : "Demo Mode".Localize());

				Button detailsLink = theme.ButtonFactory.Generate("Unlock".Localize(), AggContext.StaticData.LoadIcon("locked.png", 16, 16));
				detailsLink.BackgroundColor = theme.Colors.PrimaryAccentColor.AdjustContrast(theme.Colors.PrimaryTextColor, 10).ToColor();
				detailsLink.Margin = new BorderDouble(5);
				detailsLink.Click += (s, e) =>
				{
					ApplicationController.Instance.LaunchBrowser(UnlockLinkAttribute.UnlockPageBaseUrl + unlockLink.UnlockPageLink);
				};
				row.AddChild(detailsLink);
				editControlsContainer.AddChild(row);
			}
		}

		private void AddWebPageLinkIfRequired(PPEContext context, FlowLayoutWidget editControlsContainer, ThemeConfig theme)
		{
			var unlockLink = context.item.GetType().GetCustomAttributes(typeof(WebPageLinkAttribute), true).FirstOrDefault() as WebPageLinkAttribute;
			if (unlockLink != null)
			{
				var row = CreateSettingsRow(unlockLink.Name.Localize());

				Button detailsLink = theme.ButtonFactory.Generate("Open", AggContext.StaticData.LoadIcon("internet.png", 16, 16));
				detailsLink.Margin = new BorderDouble(5);
				detailsLink.Click += (s, e) =>
				{
					ApplicationController.Instance.LaunchBrowser(unlockLink.Url);
				};
				row.AddChild(detailsLink);
				editControlsContainer.AddChild(row);
			}
		}
	}
}