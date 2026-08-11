import { ChangeDetectionStrategy, Component, EventEmitter, Input, Output } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RepositoryRegistration, RepositoryRegistrationRequest, ReviewKind } from '../quality-api';

@Component({
  selector: 'qs-repository-dialog',
  imports: [FormsModule],
  templateUrl: './repository-dialog.html',
  styleUrl: './repository-dialog.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RepositoryDialog {
  @Input({ required: true }) repositories: RepositoryRegistration[] = [];
  @Input({ required: true }) editingRepositoryId: string | null = null;
  @Input() editingRepository: RepositoryRegistration | null = null;
  @Input({ required: true }) form!: RepositoryRegistrationRequest;
  @Input({ required: true }) tokenCapText = '';
  @Input() tokenCapError = '';
  @Input() error = '';
  @Input() saving = false;
  @Input({ required: true }) reviewKinds: ReviewKind[] = [];

  @Output() closed = new EventEmitter<void>();
  @Output() addRequested = new EventEmitter<void>();
  @Output() editRequested = new EventEmitter<RepositoryRegistration>();
  @Output() tokenCapChanged = new EventEmitter<string>();
  @Output() tokenCapBlurred = new EventEmitter<void>();
  @Output() costCapChanged = new EventEmitter<number | null>();
  @Output() reviewKindChanged = new EventEmitter<{ kind: ReviewKind; enabled: boolean }>();
  @Output() saveRequested = new EventEmitter<void>();
  @Output() archiveRequested = new EventEmitter<RepositoryRegistration>();
}
