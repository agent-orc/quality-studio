import { ChangeDetectionStrategy, Component, model } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RepositoryTrustLevel } from '../quality-api';

@Component({
  selector: 'qs-repository-trust-selector',
  imports: [FormsModule],
  templateUrl: './repository-trust-selector.html',
  styleUrl: './repository-trust-selector.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RepositoryTrustSelector {
  readonly trustLevel = model.required<RepositoryTrustLevel>();
}
