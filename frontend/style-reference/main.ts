import { bootstrapApplication } from '@angular/platform-browser';
import { provideZoneChangeDetection } from '@angular/core';
import { StyleReference } from '../src/app/shared/ui/style-reference/style-reference';

bootstrapApplication(StyleReference, { providers: [provideZoneChangeDetection()] })
  .catch(error => console.error(error));
